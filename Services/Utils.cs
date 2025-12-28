using System;
using System.Runtime.InteropServices;

namespace Synclo.Services;

public static class Utils
{
    // Fetch device name based on OS
    public static string GetDeviceName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.MachineName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Environment.MachineName;

        return "Unknown Device";
    }

    // Get persistent UUID for this device 
    public static string GetOrCreateDeviceId()
    {
        var settings = App.Settings.Settings;

        if (!string.IsNullOrWhiteSpace(settings.device_id))
            return settings.device_id;

        // generate uuid once
        var newId = Guid.NewGuid().ToString();

        settings.device_id = newId;
        App.Settings.Save();

        return newId;
    }
}