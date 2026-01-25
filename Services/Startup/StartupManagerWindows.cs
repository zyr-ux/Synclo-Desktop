using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Synclo.Services.Startup;

/// <summary>
/// Windows implementation of startup manager using Registry.
/// Manages autostart via HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
/// </summary>
public class StartupManagerWindows : IStartupManager
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Synclo";

    public bool IsSupported => true;

    public Task<bool> IsEnabledAsync()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            var value = key?.GetValue(AppName);
            return Task.FromResult(value != null);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task EnableAsync()
    {
        try
        {
            var executablePath = GetExecutablePath();
            
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            key?.SetValue(AppName, $"\"{executablePath}\" --autostart");
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to enable autostart", ex);
        }
    }

    public Task DisableAsync()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            if (key?.GetValue(AppName) != null)
            {
                key.DeleteValue(AppName);
            }
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to disable autostart", ex);
        }
    }

    private static string GetExecutablePath()
    {
        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName ?? throw new InvalidOperationException("Could not determine executable path");
    }
}
