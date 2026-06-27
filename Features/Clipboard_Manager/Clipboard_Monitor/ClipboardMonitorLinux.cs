using Microsoft.Extensions.Logging;
using Synclo.Utilities;

namespace Synclo.Features.Clipboard_Manager.Clipboard_Monitor;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class ClipboardMonitorLinux(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorLinux> logger,
    IUtils utils)
    : ClipboardMonitorBase(clipboardProvider, logger,utils);