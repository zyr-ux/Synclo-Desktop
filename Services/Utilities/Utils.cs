using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Synclo.Services.Utilities;

public interface IUtils
{
    string GetDeviceName();
    string GetOrCreateDeviceId();
    string ComputeHash(string content);
    DateTime TruncateToMilliseconds(DateTime dateTime);
}

public sealed class Utils(ISettingsService settingsService) : IUtils
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

    public string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    public DateTime TruncateToMilliseconds(DateTime dateTime)
    {
        return new DateTime(
            dateTime.Year,
            dateTime.Month,
            dateTime.Day,
            dateTime.Hour,
            dateTime.Minute,
            dateTime.Second,
            dateTime.Millisecond,
            dateTime.Kind
        );
    }
}