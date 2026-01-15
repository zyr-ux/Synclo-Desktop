using Microsoft.Extensions.Logging;

namespace Synclo.Services.ClipboardService.ClipboardMonitoringService;

public class ClipboardMonitorWindows(IClipboardProvider clipboardProvider, 
    ILogger<ClipboardMonitorWindows> logger,
    IUtils utils)
    : ClipboardMonitorBase(clipboardProvider, logger,utils);