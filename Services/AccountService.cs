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
        // TODO: Phase 2 - Implement E2EE login flow
        // 1. Fetch salt from /auth/salt
        // 2. Derive auth_key from password + salt
        // 3. Call AuthService with auth_key
        // 4. Decrypt and store MK
        
        throw new NotImplementedException("E2EE login flow not yet implemented. See Phase 2 migration.");
        
        /*
        var req = new LoginRequest
        {
            email = email,
            auth_key = "derived_from_password", // TODO: Implement KDF
            device_id = Utils.GetOrCreateDeviceId(),
            device_name = Utils.GetDeviceName()
        };

        await api.AuthService.LoginAsyncInt(req);

        settings.Settings.device_id = req.device_id;
        settings.Settings.device_name = req.device_name;
        settings.Save();
        */
    }

    public async Task RegisterAsync(string email, string password)
    {
        // TODO: Phase 2 - Implement E2EE registration flow
        // 1. Generate salt and MK
        // 2. Derive auth_key from password + salt
        // 3. Wrap MK with auth_key
        // 4. Call AuthService with E2EE credentials
        
        throw new NotImplementedException("E2EE registration flow not yet implemented. See Phase 2 migration.");
        
        /*
        var req = new RegisterRequest
        {
            email = email,
            auth_key = "derived_from_password", // TODO: Implement KDF
            encrypted_master_key = "wrapped_mk", // TODO: Implement wrapping
            salt = "generated_salt", // TODO: Generate
            kdf_version = 1,
            device_id = Utils.GetOrCreateDeviceId(),
            device_name = Utils.GetDeviceName()
        };

        await api.AuthService.RegisterAsyncInt(req);

        settings.Settings.device_id = req.device_id;
        settings.Settings.device_name = req.device_name;
        settings.Save();
        */
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
                /* Ignore */
            }

        // Wipe Secrets (Tokens, Email)
        await api.AuthService.LogoutAsyncInt();

        // Clear Cache
        await deviceCacheService.ClearAsync();
    }
}