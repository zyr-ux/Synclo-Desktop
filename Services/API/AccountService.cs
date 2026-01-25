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
    private readonly IApiService api;
    private readonly HttpClient http;
    private readonly ISettingsService settings;
    private readonly IDeviceService deviceService;
    private readonly ICryptographyService cryptographyService;
    private readonly ISecureStorage secureStorage;
    private readonly IUtils utils;
    private readonly IRefreshTokenService refreshTokenService;
    private readonly IWebSocketService webSocketService;
    private readonly INotificationService notificationService;

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
        this.api = api;
        this.http = http;
        this.settings = settings;
        this.deviceService = deviceService;
        this.cryptographyService = cryptographyService;
        this.secureStorage = secureStorage;
        this.utils = utils;
        this.refreshTokenService = refreshTokenService;
        this.webSocketService = webSocketService;
        this.notificationService = notificationService;

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
        var token = await secureStorage.LoadAsync(AccessToken);
        return !string.IsNullOrWhiteSpace(token);
    }

    public async Task<string?> GetStoredEmailAsync() => await secureStorage.LoadAsync(UserEmail);

    private void OnDeviceDeletedHandler()
    {
        // Device was deleted remotely - trigger logout
        _ = Task.Run(async () =>
        {
            try
            {
                notificationService.ShowWarning("This device has been logged out remotely");
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
        var stored = await secureStorage.LoadAsync(cryptographyService.KdfVersion);

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

            var salt = cryptographyService.FromBase64(saltResponse.salt);

            var (authKey, wrappingKey) =
                cryptographyService.DerivePasswordKeys(password, salt);

            var req = new LoginRequest
            {
                email = email,
                auth_key = cryptographyService.ToBase64(authKey),
                device_id = utils.GetOrCreateDeviceId(),
                device_name = utils.GetDeviceName()
            };

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/login")
            {
                Content = api.Serialize(req)
            };
            using var res = await http.SendAsync(httpReq, ct);
            var content = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                throw new InvalidCredentialsException(content);

            if (!res.IsSuccessStatusCode)
                throw new ServerFailureException(content);

            var data = api.Deserialize<AuthResponse>(content)
                       ?? throw new ServerFailureException("Invalid response");

            if (string.IsNullOrWhiteSpace(data.access_token) ||
                string.IsNullOrWhiteSpace(data.refresh_token) ||
                string.IsNullOrWhiteSpace(data.encrypted_master_key))
                throw new ServerFailureException("Missing auth data");

            var wrappedMk =
                cryptographyService.FromBase64(data.encrypted_master_key);

            var masterKey =
                cryptographyService.UnwrapMasterKey(wrappedMk, wrappingKey);

            await secureStorage.SaveAsync(AccessToken, data.access_token);
            await secureStorage.SaveAsync(RefreshToken, data.refresh_token);
            await secureStorage.SaveAsync(UserEmail, email);
            await secureStorage.SaveAsync(
                cryptographyService.MasterKey,
                cryptographyService.ToBase64(masterKey));
            await secureStorage.SaveAsync(
                cryptographyService.KdfVersion,
                SupportedKdfVersion.ToString());

            if (!string.IsNullOrWhiteSpace(data.salt))
                await secureStorage.SaveAsync(cryptographyService.Salt, data.salt);

            settings.Settings.device_id = req.device_id;
            settings.Settings.device_name = req.device_name;
            settings.Save();

            // Notify subscribers (e.g., ClipboardSyncService) of successful login
            if (OnLogin != null) await OnLogin.Invoke();
            
            // Connect WebSocket for real-time sync
            _ = webSocketService.ConnectAsync();
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
            var salt = cryptographyService.GenerateNonce(32);
            var masterKey = cryptographyService.GenerateMasterKey();

            var (authKey, wrappingKey) =
                cryptographyService.DerivePasswordKeys(password, salt);

            var wrappedMk =
                cryptographyService.WrapMasterKey(masterKey, wrappingKey);

            var req = new RegisterRequest
            {
                email = email,
                auth_key = cryptographyService.ToBase64(authKey),
                encrypted_master_key = cryptographyService.ToBase64(wrappedMk),
                salt = cryptographyService.ToBase64(salt),
                kdf_version = SupportedKdfVersion,
                device_id = utils.GetOrCreateDeviceId(),
                device_name = utils.GetDeviceName()
            };

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/register")
            {
                Content = api.Serialize(req)
            };
            using var res = await http.SendAsync(httpReq, ct);
            var content = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                throw res.StatusCode switch
                {
                    HttpStatusCode.Conflict => new UserAlreadyExistsException(content),
                    HttpStatusCode.BadRequest => new InvalidRequestException(content),
                    _ => new ServerFailureException(content)
                };

            var data = api.Deserialize<AuthResponse>(content)
                       ?? throw new ServerFailureException("Invalid response");

            if (string.IsNullOrWhiteSpace(data.access_token) ||
                string.IsNullOrWhiteSpace(data.refresh_token))
                throw new ServerFailureException("Missing tokens");

            await secureStorage.SaveAsync(AccessToken, data.access_token);
            await secureStorage.SaveAsync(RefreshToken, data.refresh_token);
            await secureStorage.SaveAsync(UserEmail, email);
            await secureStorage.SaveAsync(
                cryptographyService.MasterKey,
                cryptographyService.ToBase64(masterKey));
            await secureStorage.SaveAsync(
                cryptographyService.Salt,
                cryptographyService.ToBase64(salt));
            await secureStorage.SaveAsync(
                cryptographyService.KdfVersion,
                SupportedKdfVersion.ToString());

            settings.Settings.device_id = req.device_id;
            settings.Settings.device_name = req.device_name;
            settings.Save();

            // Notify subscribers (e.g., ClipboardSyncService) of successful registration
            if (OnLogin != null) await OnLogin.Invoke();
            
            // Connect WebSocket for real-time sync
            _ = webSocketService.ConnectAsync();
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

        var storedSalt = await secureStorage.LoadAsync(cryptographyService.Salt);
        var storedMk = await secureStorage.LoadAsync(cryptographyService.MasterKey);

        if (string.IsNullOrWhiteSpace(storedSalt) ||
            string.IsNullOrWhiteSpace(storedMk))
            throw new SessionExpiredException();

        var salt = cryptographyService.FromBase64(storedSalt);
        var masterKey = cryptographyService.FromBase64(storedMk);

        var (oldAuthKey, _) =
            cryptographyService.DerivePasswordKeys(currentPassword, salt);

        var newSalt = cryptographyService.GenerateNonce(32);
        var (newAuthKey, newWrappingKey) =
            cryptographyService.DerivePasswordKeys(newPassword, newSalt);

        var newWrappedMk =
            cryptographyService.WrapMasterKey(masterKey, newWrappingKey);

        var req = new PasswordChangeRequest
        {
            old_auth_key = cryptographyService.ToBase64(oldAuthKey),
            new_auth_key = cryptographyService.ToBase64(newAuthKey),
            new_encrypted_master_key = cryptographyService.ToBase64(newWrappedMk),
            new_salt = cryptographyService.ToBase64(newSalt),
            new_kdf_version = SupportedKdfVersion
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/password/change")
        {
            Content = api.Serialize(req)
        };
        
        var token = await secureStorage.LoadAsync(AccessToken);
        if (!string.IsNullOrWhiteSpace(token))
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var res = await http.SendAsync(httpReq, ct);
        var content = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidCredentialsException(content);

        if (!res.IsSuccessStatusCode)
            throw new ServerFailureException(content);

        await secureStorage.SaveAsync(
            cryptographyService.Salt,
            cryptographyService.ToBase64(newSalt));
        await secureStorage.SaveAsync(
            cryptographyService.KdfVersion,
            SupportedKdfVersion.ToString());
    }



    // -------------------- LOGIN / LOGOUT EVENTS --------------------

    public event Func<Task>? OnLogin;
    public event Func<Task>? OnLogout;

    public async Task LogoutAsync()
    {
        if (OnLogout != null)
        {
            await OnLogout.Invoke();
        }

        if (!string.IsNullOrWhiteSpace(settings.Settings.device_id) && await IsAuthenticatedAsync())
        {
            try { await deviceService.DeleteDeviceAsync(settings.Settings.device_id); }
            catch { }
        }

        // Fix: Explicitly disconnect WebSocket to fail-safe against orphaned connections
        await webSocketService.DisconnectAsync();

        await secureStorage.DeleteAsync(AccessToken);
        await secureStorage.DeleteAsync(RefreshToken);
        await secureStorage.DeleteAsync(UserEmail);
        await secureStorage.DeleteAsync(cryptographyService.MasterKey);
        await secureStorage.DeleteAsync(cryptographyService.Salt);
        await secureStorage.DeleteAsync(cryptographyService.KdfVersion);

        await deviceService.ClearAsync();
    }

    // -------------------- SALT --------------------

    private async Task<SaltResponse> GetSaltAsync(string email, CancellationToken ct)
    {
        using var res =
            await http.GetAsync($"/api/auth/salt?email={Uri.EscapeDataString(email)}", ct);

        var content = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidRequestException("User not found");

        if (!res.IsSuccessStatusCode)
            throw new ServerFailureException(content);

        return api.Deserialize<SaltResponse>(content)
               ?? throw new ServerFailureException("Missing salt");
    }
    
    // Delete Account
    public async Task DeleteAccountAsync(CancellationToken ct = default)
    {
        using var res = await api.DeleteAsync("/api/delete", ct);

        if (!res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadAsStringAsync(ct);
            throw new ServerFailureException(content); 
        }

        await LogoutAsync();
    }
}
