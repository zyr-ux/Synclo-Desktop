using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

public sealed class AccountService(APIService api, HttpClient http, ISettingsService settings, DeviceService deviceService)
{
    public const string Prefix = "com.synclo.app";
    public const string AccessToken = $"{Prefix}.auth.access_token";
    public const string RefreshToken = $"{Prefix}.auth.refresh_token";
    public const string UserEmail = $"{Prefix}.user.email";

    public async Task<bool> IsAuthenticatedAsync()
    {
        var token = await SecureStorage.LoadAsync(AccessToken);
        return !string.IsNullOrWhiteSpace(token);
    }

    public async Task<string?> GetStoredEmailAsync()
    {
        return await SecureStorage.LoadAsync(UserEmail);
    }

    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidRequestException("Email and password are required");

        try
        {
            var saltResponse = await GetSaltAsync(email, ct);
            var salt = CryptographyService.FromBase64Static(saltResponse.salt);
            var authKey = api.CryptographyService.DeriveAuthKey(password, salt);

            var req = new LoginRequest
            {
                email = email,
                auth_key = CryptographyService.ToBase64Static(authKey),
                device_id = Utils.GetOrCreateDeviceId(),
                device_name = Utils.GetDeviceName()
            };

            using var res = await api.PostAsync("/api/login", req, ct);
            var content = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                throw new InvalidCredentialsException(content);

            if (!res.IsSuccessStatusCode)
                throw new ServerFailureException(content);

            var data = api.Deserialize<AuthResponse>(content);

            if (string.IsNullOrWhiteSpace(data?.access_token) || string.IsNullOrWhiteSpace(data?.refresh_token))
                throw new ServerFailureException("Server returned success but tokens were empty.");

            await SecureStorage.SaveAsync(AccessToken, data.access_token);
            await SecureStorage.SaveAsync(RefreshToken, data.refresh_token);
            await SecureStorage.SaveAsync(UserEmail, email);

            if (!string.IsNullOrWhiteSpace(data.encrypted_master_key))
                await SecureStorage.SaveAsync(CryptographyService.MasterKey, data.encrypted_master_key);

            if (!string.IsNullOrWhiteSpace(data.salt))
                await SecureStorage.SaveAsync(CryptographyService.Salt, data.salt);

            if (data.kdf_version.HasValue)
                await SecureStorage.SaveAsync(CryptographyService.KdfVersion, data.kdf_version.Value.ToString());

            if (!string.IsNullOrWhiteSpace(data.encrypted_master_key))
            {
                var wrappedMk = CryptographyService.FromBase64Static(data.encrypted_master_key);
                var masterKey = api.CryptographyService.UnwrapMasterKey(wrappedMk, authKey);
                await SecureStorage.SaveAsync(CryptographyService.MasterKey, CryptographyService.ToBase64Static(masterKey));
            }

            settings.Settings.device_id = req.device_id;
            settings.Settings.device_name = req.device_name;
            settings.Save();
        }
        catch (Exception ex) when (ex is NotImplementedException)
        {
            throw;
        }
        catch (InvalidRequestException)
        {
            throw;
        }
        catch (InvalidCredentialsException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ServerFailureException($"Login failed: {ex.Message}");
        }
    }

    public async Task RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidRequestException("Email and password are required");

        try
        {
            var salt = api.CryptographyService.GenerateNonce(32);
            var masterKey = api.CryptographyService.GenerateMasterKey();
            var authKey = api.CryptographyService.DeriveAuthKey(password, salt);
            var wrappedMk = api.CryptographyService.WrapMasterKey(masterKey, authKey);

            var req = new RegisterRequest
            {
                email = email,
                auth_key = CryptographyService.ToBase64Static(authKey),
                encrypted_master_key = CryptographyService.ToBase64Static(wrappedMk),
                salt = CryptographyService.ToBase64Static(salt),
                kdf_version = 1,
                device_id = Utils.GetOrCreateDeviceId(),
                device_name = Utils.GetDeviceName()
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

            var data = api.Deserialize<AuthResponse>(content);

            if (string.IsNullOrWhiteSpace(data?.access_token) || string.IsNullOrWhiteSpace(data?.refresh_token))
                throw new ServerFailureException("Missing tokens in response.");

            await SecureStorage.SaveAsync(AccessToken, data.access_token);
            await SecureStorage.SaveAsync(RefreshToken, data.refresh_token);
            await SecureStorage.SaveAsync(UserEmail, email);

            if (!string.IsNullOrWhiteSpace(data.encrypted_master_key))
                await SecureStorage.SaveAsync(CryptographyService.MasterKey, data.encrypted_master_key);

            if (!string.IsNullOrWhiteSpace(data.salt))
                await SecureStorage.SaveAsync(CryptographyService.Salt, data.salt);

            if (data.kdf_version.HasValue)
                await SecureStorage.SaveAsync(CryptographyService.KdfVersion, data.kdf_version.Value.ToString());

            await SecureStorage.SaveAsync(CryptographyService.Salt, CryptographyService.ToBase64Static(salt));
            await SecureStorage.SaveAsync(CryptographyService.MasterKey, CryptographyService.ToBase64Static(masterKey));

            settings.Settings.device_id = req.device_id;
            settings.Settings.device_name = req.device_name;
            settings.Save();
        }
        catch (Exception ex) when (ex is NotImplementedException)
        {
            throw;
        }
        catch (InvalidRequestException)
        {
            throw;
        }
        catch (UserAlreadyExistsException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ServerFailureException($"Registration failed: {ex.Message}");
        }
    }

    public async Task LogoutAsync()
    {
        var deviceId = settings.Settings.device_id;
        if (!string.IsNullOrWhiteSpace(deviceId))
            try
            {
                await api.DeviceService.DeleteDeviceAsync(deviceId);
            }
            catch
            {
                App.NotificationService.ShowError("Logout failed.");
            }

        await SecureStorage.DeleteAsync(AccessToken);
        await SecureStorage.DeleteAsync(RefreshToken);
        await SecureStorage.DeleteAsync(UserEmail);
        await SecureStorage.DeleteAsync(CryptographyService.MasterKey);
        await SecureStorage.DeleteAsync(CryptographyService.Salt);
        await SecureStorage.DeleteAsync(CryptographyService.KdfVersion);
        await deviceService.ClearAsync();
    }

    private async Task<SaltResponse> GetSaltAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty", nameof(email));

        try
        {
            using var res = await http.GetAsync($"/api/auth/salt?email={Uri.EscapeDataString(email)}", ct);
            var content = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidRequestException("User not found");

            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InvalidRequestException("Too many requests. Please wait before trying again.");

            if (!res.IsSuccessStatusCode)
                throw new ServerFailureException(content);

            var data = api.Deserialize<SaltResponse>(content);
            if (data == null)
                throw new ServerFailureException("Server returned success but salt was missing");

            return data;
        }
        catch (HttpRequestException)
        {
            throw new NetworkFailureException();
        }
    }
    
    public async Task<string> RefreshTokenAsync(CancellationToken ct)
    {
        var refreshToken = await SecureStorage.LoadAsync(RefreshToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new SessionExpiredException();

        var body = new { refresh_token = refreshToken };

        try
        {
            using var res = await http.PostAsync("/api/refresh", api.Serialize(body), ct);

            if (!res.IsSuccessStatusCode)
            {
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var content = await res.Content.ReadAsStringAsync(ct);
                    if (content.Contains("reused") || content.Contains("family"))
                    {
                        await LogoutAsync();
                        throw new SecurityBreachException("Refresh token reused. Session terminated for security.");
                    }
                }

                await LogoutAsync();
                throw new SessionExpiredException();
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            var data = api.Deserialize<AuthResponse>(json);

            if (data == null || string.IsNullOrWhiteSpace(data.access_token) ||
                string.IsNullOrWhiteSpace(data.refresh_token))
            {
                await LogoutAsync();
                throw new SessionExpiredException();
            }

            await UpdateTokensAsyncInt(data.access_token, data.refresh_token);

            if (!string.IsNullOrWhiteSpace(data.encrypted_master_key))
                await SecureStorage.SaveAsync(CryptographyService.MasterKey, data.encrypted_master_key);

            if (!string.IsNullOrWhiteSpace(data.salt))
                await SecureStorage.SaveAsync(CryptographyService.Salt, data.salt);

            if (data.kdf_version.HasValue)
                await SecureStorage.SaveAsync(CryptographyService.KdfVersion, data.kdf_version.Value.ToString());

            return data.access_token;
        }
        catch (SessionExpiredException)
        {
            throw;
        }
        catch (SecurityBreachException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new NetworkFailureException();
        }
    }

    private static async Task UpdateTokensAsyncInt(string accessToken, string refreshToken)
    {
        await SecureStorage.SaveAsync(AccessToken, accessToken);
        await SecureStorage.SaveAsync(RefreshToken, refreshToken);
    }
}