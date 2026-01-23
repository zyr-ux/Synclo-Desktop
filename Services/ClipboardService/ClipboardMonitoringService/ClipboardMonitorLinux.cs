using Microsoft.Extensions.Logging;
using Synclo.Services.Utilities;

namespace Synclo.Services.ClipboardService.ClipboardMonitoringService;

public class ClipboardMonitorLinux(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorLinux> logger,
    IUtils utils)
    : ClipboardMonitorBase(clipboardProvider, logger,utils);