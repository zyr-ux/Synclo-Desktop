using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Synclo.Features.Startup_Manager;

/// <summary>
/// macOS implementation of startup manager using LaunchAgent.
/// Creates plist file in ~/Library/LaunchAgents/
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class StartupManagerMacOS : IStartupManager
{
    private const string AppIdentifier = "dev.zyrux.synclo";
    private string LaunchAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{AppIdentifier}.plist"
    );

    public bool IsSupported => true;

    public Task<bool> IsEnabledAsync()
    {
        return Task.FromResult(File.Exists(LaunchAgentPath));
    }

    public Task EnableAsync()
    {
        try
        {
            var executablePath = GetExecutablePath();
            var launchAgentDir = Path.GetDirectoryName(LaunchAgentPath);
            
            if (!string.IsNullOrEmpty(launchAgentDir))
            {
                Directory.CreateDirectory(launchAgentDir);
            }

            var plistContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{AppIdentifier}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{executablePath}</string>
        <string>--autostart</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
</dict>
</plist>";

            File.WriteAllText(LaunchAgentPath, plistContent);
            
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
            if (File.Exists(LaunchAgentPath))
            {
                File.Delete(LaunchAgentPath);
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
