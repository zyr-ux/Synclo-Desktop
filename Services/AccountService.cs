using System;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

public sealed class AccountService(
    APIService api,
    HttpClient http,
    ISettingsService settings,
    DeviceService deviceService,
    WebSocketService webSocketService,
    CryptographyService cryptographyService,
    ISecureStorage secureStorage,
    Utils utils)
{
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

    // -------------------- KDF ENFORCEMENT --------------------
    public async Task EnforceLocalKdfVersionAsync()
    {
        var stored = await secureStorage.LoadAsync(CryptographyService.KdfVersion);

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

            var salt = CryptographyService.FromBase64Static(saltResponse.salt);

            var (authKey, wrappingKey) =
                cryptographyService.DerivePasswordKeys(password, salt);

            var req = new LoginRequest
            {
                email = email,
                auth_key = CryptographyService.ToBase64Static(authKey),
                device_id = utils.GetOrCreateDeviceId(),
                device_name = utils.GetDeviceName()
            };

            using var res = await api.PostAsync("/api/login", req, ct);
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
                CryptographyService.FromBase64Static(data.encrypted_master_key);

            var masterKey =
                cryptographyService.UnwrapMasterKey(wrappedMk, wrappingKey);

            await secureStorage.SaveAsync(AccessToken, data.access_token);
            await secureStorage.SaveAsync(RefreshToken, data.refresh_token);
            await secureStorage.SaveAsync(UserEmail, email);
            await secureStorage.SaveAsync(
                CryptographyService.MasterKey,
                CryptographyService.ToBase64Static(masterKey));
            await secureStorage.SaveAsync(
                CryptographyService.KdfVersion,
                SupportedKdfVersion.ToString());

            if (!string.IsNullOrWhiteSpace(data.salt))
                await secureStorage.SaveAsync(CryptographyService.Salt, data.salt);

            settings.Settings.device_id = req.device_id;
            settings.Settings.device_name = req.device_name;
            settings.Save();
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
                auth_key = CryptographyService.ToBase64Static(authKey),
                encrypted_master_key = CryptographyService.ToBase64Static(wrappedMk),
                salt = CryptographyService.ToBase64Static(salt),
                kdf_version = SupportedKdfVersion,
                device_id = utils.GetOrCreateDeviceId(),
                device_name = utils.GetDeviceName()
            };

            using var res = await api.PostAsync("/api/register", req, ct);
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
                CryptographyService.MasterKey,
                CryptographyService.ToBase64Static(masterKey));
            await secureStorage.SaveAsync(
                CryptographyService.Salt,
                CryptographyService.ToBase64Static(salt));
            await secureStorage.SaveAsync(
                CryptographyService.KdfVersion,
                SupportedKdfVersion.ToString());

            settings.Settings.device_id = req.device_id;
            settings.Settings.device_name = req.device_name;
            settings.Save();
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

        var storedSalt = await secureStorage.LoadAsync(CryptographyService.Salt);
        var storedMk = await secureStorage.LoadAsync(CryptographyService.MasterKey);

        if (string.IsNullOrWhiteSpace(storedSalt) ||
            string.IsNullOrWhiteSpace(storedMk))
            throw new SessionExpiredException();

        var salt = CryptographyService.FromBase64Static(storedSalt);
        var masterKey = CryptographyService.FromBase64Static(storedMk);

        var (oldAuthKey, _) =
            cryptographyService.DerivePasswordKeys(currentPassword, salt);

        var newSalt = cryptographyService.GenerateNonce(32);
        var (newAuthKey, newWrappingKey) =
            cryptographyService.DerivePasswordKeys(newPassword, newSalt);

        var newWrappedMk =
            cryptographyService.WrapMasterKey(masterKey, newWrappingKey);

        var req = new
        {
            old_auth_key = CryptographyService.ToBase64Static(oldAuthKey),
            new_auth_key = CryptographyService.ToBase64Static(newAuthKey),
            new_encrypted_master_key = CryptographyService.ToBase64Static(newWrappedMk),
            new_salt = CryptographyService.ToBase64Static(newSalt),
            new_kdf_version = SupportedKdfVersion
        };

        using var res = await api.PostAsync("/api/password/change", req, ct);
        var content = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidCredentialsException(content);

        if (!res.IsSuccessStatusCode)
            throw new ServerFailureException(content);

        await secureStorage.SaveAsync(
            CryptographyService.Salt,
            CryptographyService.ToBase64Static(newSalt));
        await secureStorage.SaveAsync(
            CryptographyService.KdfVersion,
            SupportedKdfVersion.ToString());
    }

    // -------------------- TOKEN REFRESH --------------------

    public async Task<string> RefreshTokenAsync(CancellationToken ct = default)
    {
        var refreshToken = await secureStorage.LoadAsync(RefreshToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new SessionExpiredException();

        var body = new { refresh_token = refreshToken };

        try
        {
            using var res = await api.PostAsync("/api/refresh", body, ct);

            if (!res.IsSuccessStatusCode)
            {
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var errorContent = await res.Content.ReadAsStringAsync(ct);
                    if (errorContent.Contains("reused") || errorContent.Contains("family"))
                    {
                        await LogoutAsync();
                        throw new SecurityBreachException("Refresh token reuse detected.");
                    }
                }

                await LogoutAsync();
                throw new SessionExpiredException();
            }

            var content = await res.Content.ReadAsStringAsync(ct);
            var data = api.Deserialize<AuthResponse>(content);

            if (data == null ||
                string.IsNullOrWhiteSpace(data.access_token) ||
                string.IsNullOrWhiteSpace(data.refresh_token))
            {
                await LogoutAsync();
                throw new SessionExpiredException();
            }

            if (data.kdf_version.HasValue &&
                data.kdf_version.Value != SupportedKdfVersion)
            {
                await LogoutAsync();
                throw new SecurityException(
                    $"Account upgraded to security version {data.kdf_version}. Please update the app.");
            }

            await secureStorage.SaveAsync(AccessToken, data.access_token);
            await secureStorage.SaveAsync(RefreshToken, data.refresh_token);

            if (!string.IsNullOrWhiteSpace(data.salt))
                await secureStorage.SaveAsync(CryptographyService.Salt, data.salt);

            if (data.kdf_version.HasValue)
                await secureStorage.SaveAsync(
                    CryptographyService.KdfVersion,
                    data.kdf_version.Value.ToString());

            return data.access_token;
        }
        catch (HttpRequestException)
        {
            throw new NetworkFailureException();
        }
    }

    // -------------------- LOGOUT --------------------

    public async Task LogoutAsync()
    {
        await webSocketService.DisconnectAsync();
        var deviceId = settings.Settings.device_id;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try { await deviceService.DeleteDeviceAsync(deviceId); }
            catch { }
        }

        await secureStorage.DeleteAsync(AccessToken);
        await secureStorage.DeleteAsync(RefreshToken);
        await secureStorage.DeleteAsync(UserEmail);
        await secureStorage.DeleteAsync(CryptographyService.MasterKey);
        await secureStorage.DeleteAsync(CryptographyService.Salt);
        await secureStorage.DeleteAsync(CryptographyService.KdfVersion);

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
        using var res = await api.DeleteAsync("/api/account/delete", ct);

        if (!res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadAsStringAsync(ct);
            throw new ServerFailureException(content); 
        }

        await LogoutAsync();
    }
}
