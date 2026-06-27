using System;
using Microsoft.Extensions.Logging;
using Synclo.Utilities;

namespace Synclo.Features.Clipboard_Manager.Clipboard_Monitor;

public static class ClipboardMonitorFactory
{
    public static IClipboardMonitor GetClipboardMonitor(
        IClipboardProvider clipboardProvider,
        ILoggerFactory loggerFactory,
        IUtils utils)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ClipboardMonitorWindows(
                clipboardProvider,
                loggerFactory.CreateLogger<ClipboardMonitorWindows>(),
                utils);
        }
        if (OperatingSystem.IsMacOS())
        {
            return new ClipboardMonitorMacOS(
                clipboardProvider,
                loggerFactory.CreateLogger<ClipboardMonitorMacOS>(),
                utils);
        }
        if (OperatingSystem.IsLinux())
        {
            return new ClipboardMonitorLinux(
                clipboardProvider,
                loggerFactory.CreateLogger<ClipboardMonitorLinux>(),
                utils);
        }

        throw new PlatformNotSupportedException("Clipboard monitor is not supported on this platform.");
    }
}
