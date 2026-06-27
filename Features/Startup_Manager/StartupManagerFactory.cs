using System;

namespace Synclo.Features.Startup_Manager;

public static class StartupManagerFactory
{
    public static IStartupManager GetStartupManager()
    {
        if (OperatingSystem.IsWindows())
        {
            return new StartupManagerWindows();
        }
        if (OperatingSystem.IsMacOS())
        {
            return new StartupManagerMacOS();
        }
        if (OperatingSystem.IsLinux())
        {
            return new StartupManagerLinux();
        }

        throw new PlatformNotSupportedException("Startup manager is not supported on this platform.");
    }
}
