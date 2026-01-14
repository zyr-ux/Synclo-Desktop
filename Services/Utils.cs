using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Synclo.Services;

public sealed class Utils(ISettingsService settingsService)
{
    public string GetDeviceName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.MachineName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Environment.MachineName;

        return "Unknown Device";
    }

    public string GetOrCreateDeviceId()
    {
        var settings = settingsService.Settings;

        if (!string.IsNullOrWhiteSpace(settings.device_id))
            return settings.device_id;

        var newId = Guid.NewGuid().ToString();

        settings.device_id = newId;
        settingsService.Save();

        return newId;
    }

    public static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}