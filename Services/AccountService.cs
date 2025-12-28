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
        var req = new LoginRequest
        {
            email = email,
            password = password,
            device_id = Utils.GetOrCreateDeviceId(),
            device_name = Utils.GetDeviceName()
        };

        // API Service now saves tokens & email to Secrets Manager internally 
        // as per our previous refactor
        await api.AuthService.LoginAsyncInt(req);

        // Store non-sensitive metadata in standard settings
        settings.Settings.device_id = req.device_id;
        settings.Settings.device_name = req.device_name;
        settings.Save();
    }

    public async Task RegisterAsync(string email, string password)
    {
        var req = new RegisterRequest
        {
            email = email,
            password = password,
            device_id = Utils.GetOrCreateDeviceId(),
            device_name = Utils.GetDeviceName()
        };

        await api.AuthService.RegisterAsyncInt(req);

        settings.Settings.device_id = req.device_id;
        settings.Settings.device_name = req.device_name;
        settings.Save();
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