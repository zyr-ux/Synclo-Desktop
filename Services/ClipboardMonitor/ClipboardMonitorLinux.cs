using Microsoft.Extensions.Logging;
using Synclo.Services.Utilities;

namespace Synclo.Services.ClipboardMonitor;

public class ClipboardMonitorLinux(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorLinux> logger,
    IUtils utils)
    : ClipboardMonitorBase(clipboardProvider, logger,utils);