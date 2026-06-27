using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Synclo.Features.Startup_Manager;

/// <summary>
/// Linux implementation of startup manager using .desktop file.
/// Creates autostart entry in ~/.config/autostart/
/// </summary>
public class StartupManagerLinux : IStartupManager
{
    private const string AppName = "synclo";
    private string AutostartFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart", $"{AppName}.desktop"
    );

    public bool IsSupported => true;

    public Task<bool> IsEnabledAsync()
    {
        return Task.FromResult(File.Exists(AutostartFilePath));
    }

    public Task EnableAsync()
    {
        try
        {
            var executablePath = GetExecutablePath();
            var autostartDir = Path.GetDirectoryName(AutostartFilePath);
            
            if (!string.IsNullOrEmpty(autostartDir))
            {
                Directory.CreateDirectory(autostartDir);
            }

            var desktopEntry = $@"[Desktop Entry]
Type=Application
Name=Synclo
Exec={executablePath} --autostart
Hidden=false
NoDisplay=false
X-GNOME-Autostart-enabled=true
";

            File.WriteAllText(AutostartFilePath, desktopEntry);
            
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
            if (File.Exists(AutostartFilePath))
            {
                File.Delete(AutostartFilePath);
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
