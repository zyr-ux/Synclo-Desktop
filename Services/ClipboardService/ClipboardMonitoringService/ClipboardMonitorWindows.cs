using Microsoft.Extensions.Logging;

namespace Synclo.Services.ClipboardService.ClipboardMonitoringService;

public class ClipboardMonitorWindows(IClipboardProvider clipboardProvider, ILogger<ClipboardMonitorWindows> logger)
    : ClipboardMonitorBase(clipboardProvider, logger);