using Microsoft.Extensions.Logging;

namespace Synclo.Services.ClipboardService.ClipboardMonitoringService;

public class ClipboardMonitorLinux(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorLinux> logger,
    IUtils utils)
    : ClipboardMonitorBase(clipboardProvider, logger,utils);