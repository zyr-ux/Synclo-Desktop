using Microsoft.Extensions.Logging;
using Synclo.Services.Utilities;

namespace Synclo.Services.ClipboardService.ClipboardMonitoringService;

public class ClipboardMonitorMacOS(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorMacOS> logger,
    IUtils utils)
    : ClipboardMonitorBase(clipboardProvider, logger,utils);