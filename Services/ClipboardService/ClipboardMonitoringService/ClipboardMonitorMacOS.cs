using Microsoft.Extensions.Logging;

namespace Synclo.Services.ClipboardService.ClipboardMonitoringService;

public class ClipboardMonitorMacOS(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorMacOS> logger,
    IUtils utils)
    : ClipboardMonitorBase(clipboardProvider, logger,utils);