using System;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

public sealed class AccountService(APIService api, ISettingsService settings, DeviceCacheService deviceCacheService)
{
    public async Task<bool> IsAuthenticatedAsync()
    {
        var token = await SecureStorage.LoadAsync(AuthService.AccessToken);
        return !string.IsNullOrWhiteSpace(token);
    }

    public async Task<string?> GetStoredEmailAsync()
    {
        return await SecureStorage.LoadAsync(AuthService.UserEmail);
    }

    public async Task LoginAsync(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidRequestException("Email and password are required");

        try
        {
            // Step 1: Fetch salt from server for password derivation
            var saltResponse = await api.AuthService.GetSaltAsyncInt(email);
            var salt = CryptographyService.FromBase64Static(saltResponse.salt);

            // Step 2: Derive auth_key from password using KDF
            var authKey = api.CryptographyService.DeriveAuthKey(password, salt);

            // Step 3: Create login request with derived auth_key
            var req = new LoginRequest
            {
                email = email,
                auth_key = CryptographyService.ToBase64Static(authKey),
                device_id = Utils.GetOrCreateDeviceId(),
                device_name = Utils.GetDeviceName()
            };

            // Step 4: Call AuthService to login and receive encrypted MK
            var response = await api.AuthService.LoginAsyncInt(req);

            // Step 5: Decrypt master key using derived auth_key
            if (!string.IsNullOrWhiteSpace(response.encrypted_master_key))
            {
                var wrappedMk = CryptographyService.FromBase64Static(response.encrypted_master_key);
                var masterKey = api.CryptographyService.UnwrapMasterKey(wrappedMk, authKey);
                
                // Step 6: Store master key securely
                await SecureStorage.SaveAsync(CryptographyService.MasterKey, CryptographyService.ToBase64Static(masterKey));
            }

            // Step 7: Update local settings
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

    public async Task RegisterAsync(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidRequestException("Email and password are required");

        try
        {
            // Step 1: Generate salt (32 random bytes) - will be stored on server
            var salt = api.CryptographyService.GenerateNonce(32);

            // Step 2: Generate master key for clipboard encryption
            var masterKey = api.CryptographyService.GenerateMasterKey();

            // Step 3: Derive auth_key from password using KDF with generated salt
            var authKey = api.CryptographyService.DeriveAuthKey(password, salt);

            // Step 4: Encrypt (wrap) master key with auth_key
            var wrappedMk = api.CryptographyService.WrapMasterKey(masterKey, authKey);

            // Step 5: Create registration request with base64-encoded E2EE credentials
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

            // Step 6: Call AuthService to register
            await api.AuthService.RegisterAsyncInt(req);

            // Step 7: Store salt and master key securely locally
            await SecureStorage.SaveAsync(CryptographyService.Salt, CryptographyService.ToBase64Static(salt));
            await SecureStorage.SaveAsync(CryptographyService.MasterKey, CryptographyService.ToBase64Static(masterKey));

            // Step 8: Update local settings
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
        // Delete this device from the server
        var deviceId = settings.Settings.device_id;
        if (!string.IsNullOrWhiteSpace(deviceId))
            try
            {
                await api.DeviceService.DeleteDeviceAsync(deviceId);
            }
            catch
            {
                /* Ignore - device deletion is best-effort */
            }

        // Wipe Secrets (Tokens, Email, and E2EE credentials)
        await api.AuthService.LogoutAsyncInt();

        // Clear Cache
        await deviceCacheService.ClearAsync();
    }
}