using System;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.Services.SecretsManager;
using Synclo.Services.Utilities;

namespace Synclo.Services.API;

public interface IAccountService
{
    Task<bool> IsAuthenticatedAsync();
    Task<string?> GetStoredEmailAsync();
    Task EnforceLocalKdfVersionAsync();
    Task LoginAsync(string email, string password, CancellationToken ct = default);
    Task RegisterAsync(string email, string password, CancellationToken ct = default);
    Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default);
    Task LogoutAsync();
    Task DeleteAccountAsync(CancellationToken ct = default);
    event Func<Task>? OnLogin;
    event Func<Task>? OnLogout;
}

public sealed class AccountService : IAccountService
{
    private readonly IApiService _api;
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly IDeviceService _deviceService;
    private readonly ICryptographyService _cryptographyService;
    private readonly ISecureStorage _secureStorage;
    private readonly IUtils _utils;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IWebSocketService _webSocketService;
    private readonly INotificationService _notificationService;
    
    // Flag to immediately reflect logout state before secure storage is cleared
    private volatile bool _isLoggingOut = false;

    public AccountService(
        IApiService api,
        HttpClient http,
        ISettingsService settings,
        IDeviceService deviceService,
        ICryptographyService cryptographyService,
        ISecureStorage secureStorage,
        IUtils utils,
        IRefreshTokenService refreshTokenService,
        IWebSocketService webSocketService,
        INotificationService notificationService)
    {
        _api = api;
        _http = http;
        _settings = settings;
        _deviceService = deviceService;
        _cryptographyService = cryptographyService;
        _secureStorage = secureStorage;
        _utils = utils;
        _refreshTokenService = refreshTokenService;
        _webSocketService = webSocketService;
        _notificationService = notificationService;

        // Subscribe to device deletion event
        webSocketService.OnDeviceDeleted += OnDeviceDeletedHandler;
    }

    public const string Prefix = "com.synclo.app";
    public const string AccessToken = $"{Prefix}.auth.access_token";
    public const string RefreshToken = $"{Prefix}.auth.refresh_token";
    public const string UserEmail = $"{Prefix}.user.email";

    // -------------------- KDF --------------------
    private const int SupportedKdfVersion = 1;

    // -------------------- AUTH STATE --------------------

    public async Task<bool> IsAuthenticatedAsync()
    {
        // Check logout flag first to avoid race condition
        if (_isLoggingOut) return false;
        
        var token = await _secureStorage.LoadAsync(AccessToken);
        return !string.IsNullOrWhiteSpace(token);
    }

    public async Task<string?> GetStoredEmailAsync() => await _secureStorage.LoadAsync(UserEmail);

    private void OnDeviceDeletedHandler(string? deletedDeviceId)
    {
        // If a specific device ID was provided, check if it matches OUR device ID
        var currentDeviceId = _settings.Settings.device_id;
        
        // If it's another device, ignore the event (do not log out)
        if (!string.IsNullOrEmpty(deletedDeviceId) && 
            !string.IsNullOrEmpty(currentDeviceId) && 
            deletedDeviceId != currentDeviceId)
        {
            return; 
        }

        // Device was deleted remotely - trigger logout
        _ = Task.Run(async () =>
        {
            try
            {
                // Ensure UI notification runs on the UI thread
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                    _notificationService.ShowWarning("This device has been logged out remotely"));
            }
            catch
            {
                // Swallow exceptions during notification (e.g. if app is closing)
            }
            
            try
            {
                // Ensure logout happens regardless of notification success
                await LogoutAsync();
            }
            catch
            {
                // Swallow exceptions during logout to prevent crashes
            }
        });
    }

    // -------------------- KDF ENFORCEMENT --------------------
    public async Task EnforceLocalKdfVersionAsync()
    {
        var stored = await _secureStorage.LoadAsync(_cryptographyService.KdfVersion);

        if (string.IsNullOrWhiteSpace(stored))
            return;

        if (!int.TryParse(stored, out var version) || version != SupportedKdfVersion)
        {
            await LogoutAsync();
            throw new SecurityException(
                $"Local session uses unsupported security version {stored}. Please log in again.");
        }
    }

    // -------------------- LOGIN --------------------

    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidRequestException("Email and password are required");

        try
        {
            var saltResponse = await GetSaltAsync(email, ct);

            if (saltResponse.kdf_version != SupportedKdfVersion)
            {
                throw new InvalidRequestException(
                    $"Account requires security version {saltResponse.kdf_version}, " +
                    $"but this app only supports version {SupportedKdfVersion}. Please update.");
            }

            var salt = _cryptographyService.FromBase64(saltResponse.salt);

            var (authKey, wrappingKey) =
                _cryptographyService.DerivePasswordKeys(password, salt);

            var req = new LoginRequest
            {
                email = email,
                auth_key = _cryptographyService.ToBase64(authKey),
                device_id = _utils.GetOrCreateDeviceId(),
                device_name = _utils.GetDeviceName()
            };

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/login")
            {
                Content = _api.Serialize(req)
            };
            using var res = await _http.SendAsync(httpReq, ct);
            var content = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                throw new InvalidCredentialsException(content);

            if (!res.IsSuccessStatusCode)
                throw new ServerFailureException(content);

            var data = _api.Deserialize<AuthResponse>(content)
                       ?? throw new ServerFailureException("Invalid response");

            if (string.IsNullOrWhiteSpace(data.access_token) ||
                string.IsNullOrWhiteSpace(data.refresh_token) ||
                string.IsNullOrWhiteSpace(data.encrypted_master_key))
                throw new ServerFailureException("Missing auth data");

            var wrappedMk =
                _cryptographyService.FromBase64(data.encrypted_master_key);

            var masterKey =
                _cryptographyService.UnwrapMasterKey(wrappedMk, wrappingKey);

            await _secureStorage.SaveAsync(AccessToken, data.access_token);
            await _secureStorage.SaveAsync(RefreshToken, data.refresh_token);
            await _secureStorage.SaveAsync(UserEmail, email);
            await _secureStorage.SaveAsync(
                _cryptographyService.MasterKey,
                _cryptographyService.ToBase64(masterKey));
            await _secureStorage.SaveAsync(
                _cryptographyService.KdfVersion,
                SupportedKdfVersion.ToString());

            if (!string.IsNullOrWhiteSpace(data.salt))
                await _secureStorage.SaveAsync(_cryptographyService.Salt, data.salt);

            _settings.Settings.device_id = req.device_id;
            _settings.Settings.device_name = req.device_name;
            _settings.Save();

            // Notify subscribers (e.g., ClipboardSyncService) of successful login
            if (OnLogin != null) await OnLogin.Invoke();
            
            // Connect WebSocket for real-time sync
            _ = _webSocketService.ConnectAsync();
        }
        catch (InvalidRequestException) { throw; }
        catch (InvalidCredentialsException) { throw; }
        catch (Exception ex)
        {
            throw new ServerFailureException($"Login failed: {ex.Message}");
        }
    }

    // -------------------- REGISTER --------------------

    public async Task RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidRequestException("Email and password are required");

        try
        {
            var salt = _cryptographyService.GenerateNonce(32);
            var masterKey = _cryptographyService.GenerateMasterKey();

            var (authKey, wrappingKey) =
                _cryptographyService.DerivePasswordKeys(password, salt);

            var wrappedMk =
                _cryptographyService.WrapMasterKey(masterKey, wrappingKey);

            var req = new RegisterRequest
            {
                email = email,
                auth_key = _cryptographyService.ToBase64(authKey),
                encrypted_master_key = _cryptographyService.ToBase64(wrappedMk),
                salt = _cryptographyService.ToBase64(salt),
                kdf_version = SupportedKdfVersion,
                device_id = _utils.GetOrCreateDeviceId(),
                device_name = _utils.GetDeviceName()
            };

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/register")
            {
                Content = _api.Serialize(req)
            };
            using var res = await _http.SendAsync(httpReq, ct);
            var content = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                throw res.StatusCode switch
                {
                    HttpStatusCode.Conflict => new UserAlreadyExistsException(content),
                    HttpStatusCode.BadRequest => new InvalidRequestException(content),
                    _ => new ServerFailureException(content)
                };

            var data = _api.Deserialize<AuthResponse>(content)
                       ?? throw new ServerFailureException("Invalid response");

            if (string.IsNullOrWhiteSpace(data.access_token) ||
                string.IsNullOrWhiteSpace(data.refresh_token))
                throw new ServerFailureException("Missing tokens");

            await _secureStorage.SaveAsync(AccessToken, data.access_token);
            await _secureStorage.SaveAsync(RefreshToken, data.refresh_token);
            await _secureStorage.SaveAsync(UserEmail, email);
            await _secureStorage.SaveAsync(
                _cryptographyService.MasterKey,
                _cryptographyService.ToBase64(masterKey));
            await _secureStorage.SaveAsync(
                _cryptographyService.Salt,
                _cryptographyService.ToBase64(salt));
            await _secureStorage.SaveAsync(
                _cryptographyService.KdfVersion,
                SupportedKdfVersion.ToString());

            _settings.Settings.device_id = req.device_id;
            _settings.Settings.device_name = req.device_name;
            _settings.Save();

            // Notify subscribers (e.g., ClipboardSyncService) of successful registration
            if (OnLogin != null) await OnLogin.Invoke();
            
            // Connect WebSocket for real-time sync
            _ = _webSocketService.ConnectAsync();
        }
        catch (InvalidRequestException) { throw; }
        catch (UserAlreadyExistsException) { throw; }
        catch (Exception ex)
        {
            throw new ServerFailureException($"Registration failed: {ex.Message}");
        }
    }

    // -------------------- CHANGE PASSWORD --------------------

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(currentPassword) ||
            string.IsNullOrWhiteSpace(newPassword))
            throw new InvalidRequestException("Passwords are required");

        var storedSalt = await _secureStorage.LoadAsync(_cryptographyService.Salt);
        var storedMk = await _secureStorage.LoadAsync(_cryptographyService.MasterKey);

        if (string.IsNullOrWhiteSpace(storedSalt) ||
            string.IsNullOrWhiteSpace(storedMk))
            throw new SessionExpiredException();

        var salt = _cryptographyService.FromBase64(storedSalt);
        var masterKey = _cryptographyService.FromBase64(storedMk);

        var (oldAuthKey, _) =
            _cryptographyService.DerivePasswordKeys(currentPassword, salt);

        var newSalt = _cryptographyService.GenerateNonce(32);
        var (newAuthKey, newWrappingKey) =
            _cryptographyService.DerivePasswordKeys(newPassword, newSalt);

        var newWrappedMk =
            _cryptographyService.WrapMasterKey(masterKey, newWrappingKey);

        var req = new PasswordChangeRequest
        {
            old_auth_key = _cryptographyService.ToBase64(oldAuthKey),
            new_auth_key = _cryptographyService.ToBase64(newAuthKey),
            new_encrypted_master_key = _cryptographyService.ToBase64(newWrappedMk),
            new_salt = _cryptographyService.ToBase64(newSalt),
            new_kdf_version = SupportedKdfVersion
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/password/change")
        {
            Content = _api.Serialize(req)
        };
        
        var token = await _secureStorage.LoadAsync(AccessToken);
        if (!string.IsNullOrWhiteSpace(token))
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var res = await _http.SendAsync(httpReq, ct);
        var content = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidCredentialsException(content);

        if (!res.IsSuccessStatusCode)
            throw new ServerFailureException(content);

        await _secureStorage.SaveAsync(
            _cryptographyService.Salt,
            _cryptographyService.ToBase64(newSalt));
        await _secureStorage.SaveAsync(
            _cryptographyService.KdfVersion,
            SupportedKdfVersion.ToString());
    }



    // -------------------- LOGIN / LOGOUT EVENTS --------------------

    public event Func<Task>? OnLogin;
    public event Func<Task>? OnLogout;

    public async Task LogoutAsync()
    {
        // Set flag immediately to make IsAuthenticatedAsync() return false
        _isLoggingOut = true;
        
        if (OnLogout != null)
        {
            await OnLogout.Invoke();
        }

        if (!string.IsNullOrWhiteSpace(_settings.Settings.device_id) && await IsAuthenticatedAsync())
        {
            try { await _deviceService.DeleteDeviceAsync(_settings.Settings.device_id); }
            catch { }
        }

        // Fix: Explicitly disconnect WebSocket to fail-safe against orphaned connections
        await _webSocketService.DisconnectAsync();

        await _secureStorage.DeleteAsync(AccessToken);
        await _secureStorage.DeleteAsync(RefreshToken);
        await _secureStorage.DeleteAsync(UserEmail);
        await _secureStorage.DeleteAsync(_cryptographyService.MasterKey);
        await _secureStorage.DeleteAsync(_cryptographyService.Salt);
        await _secureStorage.DeleteAsync(_cryptographyService.KdfVersion);

        await _deviceService.ClearAsync();
        
        // Reset flag after logout completes (for potential re-login)
        _isLoggingOut = false;
    }

    // -------------------- SALT --------------------

    private async Task<SaltResponse> GetSaltAsync(string email, CancellationToken ct)
    {
        using var res =
            await _http.GetAsync($"/api/auth/salt?email={Uri.EscapeDataString(email)}", ct);

        var content = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidRequestException("User not found");

        if (!res.IsSuccessStatusCode)
            throw new ServerFailureException(content);

        return _api.Deserialize<SaltResponse>(content)
               ?? throw new ServerFailureException("Missing salt");
    }
    
    // Delete Account
    public async Task DeleteAccountAsync(CancellationToken ct = default)
    {
        using var res = await _api.DeleteAsync("/api/delete", ct);

        if (!res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadAsStringAsync(ct);
            throw new ServerFailureException(content); 
        }

        await LogoutAsync();
    }
}
