using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Synclo.Features.Settings_Manager;
using Synclo.Models;

namespace Synclo.Utilities;

public interface IUtils
{
    string GetDeviceName();
    string GetClientOS();
    string GetOrCreateDeviceId();
    string ComputeHash(string content);
    DateTime TruncateToMilliseconds(DateTime dateTime);
    void OpenUrl(string url);
    bool TryNormalizeServerUrl(string? raw, out string normalized, out string? error);
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

    public string GetClientOS()
    {
        return RuntimeInformation.OSDescription;
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

    public void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }
        catch
        {
            // Suppress launch failures
        }
    }

    public bool TryNormalizeServerUrl(string? raw, out string normalized, out string? error)
    {
        var input = raw?.Trim() ?? "";

        // Blank input or explicit default → fall back to the canonical default
        if (string.IsNullOrWhiteSpace(input))
        {
            normalized = AppSettings.DefaultServerUrl;
            error = null;
            return true;
        }

        // If the user typed the default URL exactly, accept it as-is
        if (string.Equals(input, AppSettings.DefaultServerUrl, StringComparison.OrdinalIgnoreCase))
        {
            normalized = AppSettings.DefaultServerUrl;
            error = null;
            return true;
        }

        // Prepend https:// when the user omits the scheme
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            input = "https://" + input;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var tempUri) ||
            (tempUri.Scheme != Uri.UriSchemeHttp && tempUri.Scheme != Uri.UriSchemeHttps))
        {
            normalized = "";
            error = "Invalid URL format. Please enter a valid HTTP/HTTPS URL.";
            return false;
        }

        var host = tempUri.Host;
        if (host != "localhost" && 
            Uri.CheckHostName(host) != UriHostNameType.IPv4 && 
            Uri.CheckHostName(host) != UriHostNameType.IPv6)
        {
            var parts = host.Split('.');
            bool isValid = parts.Length >= 2;
            if (isValid)
            {
                foreach (var part in parts)
                {
                    if (string.IsNullOrWhiteSpace(part))
                    {
                        isValid = false;
                        break;
                    }
                }

                if (isValid && parts[^1].Length < 2)
                {
                    isValid = false;
                }
            }

            if (!isValid)
            {
                normalized = "";
                error = "Invalid URL format. Please enter a valid HTTP/HTTPS URL.";
                return false;
            }
        }

        input = tempUri.ToString().TrimEnd('/');

        // Strip a trailing /api/v1 suffix so users can paste the full API endpoint URL
        if (input.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            input = input[..^"/api/v1".Length].TrimEnd('/');

        if (!Uri.TryCreate(input, UriKind.Absolute, out var finalUri) ||
            (finalUri.Scheme != Uri.UriSchemeHttp && finalUri.Scheme != Uri.UriSchemeHttps))
        {
            normalized = "";
            error = "Invalid URL format.";
            return false;
        }

        normalized = finalUri.ToString().TrimEnd('/');
        error = null;
        return true;
    }
}