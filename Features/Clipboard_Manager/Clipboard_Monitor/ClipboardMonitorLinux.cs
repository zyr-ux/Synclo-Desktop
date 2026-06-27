using Microsoft.Extensions.Logging;
using Synclo.Utilities;

namespace Synclo.Features.Clipboard_Manager.Clipboard_Monitor;

public class ClipboardMonitorLinux(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorLinux> logger,
    IUtils utils)
    : ClipboardMonitorBase(clipboardProvider, logger,utils);