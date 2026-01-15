using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Synclo.Services.ClipboardService.ClipboardMonitoringService;

public interface IClipboardProvider
{
    Task<string?> GetTextAsync();
    Task SetTextAsync(string text);
}

public class AvaloniaClipboardProvider : IClipboardProvider
{
    public async Task<string?> GetTextAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = desktop.MainWindow?.Clipboard;
                if (clipboard != null)
                {
                    return await clipboard.GetTextAsync();
                }
            }
            return null;
        });
    }

    public async Task SetTextAsync(string text)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = desktop.MainWindow?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(text);
                }
            }
        });
    }
}

/// <summary>
/// Base class for clipboard monitors with shared polling logic
/// </summary>
public abstract class ClipboardMonitorBase(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorBase> logger,
    IUtils utils,
    int pollingIntervalMs = ClipboardMonitorBase.DefaultPollingIntervalMs)
    : IClipboardMonitor, IDisposable
{
    private const int DefaultPollingIntervalMs = 500;
    private readonly IClipboardProvider _clipboardProvider = clipboardProvider ?? throw new ArgumentNullException(nameof(clipboardProvider));
    private readonly ILogger<ClipboardMonitorBase> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IUtils _utils = utils ?? throw new ArgumentNullException(nameof(utils));
    private CancellationTokenSource? _cts;
    private Task? _monitoringTask;
    private string? _lastClipboardHash;
    private bool _disposed;

    // Implementation of IClipboardMonitor interface members
    public event Action<string>? OnClipboardChanged;
    public bool IsRunning => _cts != null && !_cts.Token.IsCancellationRequested;

    public virtual async Task StartAsync()
    {
        if (IsRunning) return;
        
        _cts = new CancellationTokenSource();
        
        // Initialize hash on startup to avoid immediately firing for pre-existing content
        await InitializeClipboardHashAsync();
        
        _monitoringTask = Task.Run(async () => await MonitorClipboardLoop(_cts.Token));
    }

    private async Task InitializeClipboardHashAsync()
    {
        try
        {
            var clipboardText = await _clipboardProvider.GetTextAsync();
            if (!string.IsNullOrEmpty(clipboardText))
            {
                _lastClipboardHash = _utils.ComputeHash(clipboardText);
                _logger.LogInformation("Clipboard hash initialized successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error initializing clipboard hash");
        }
    }

    public virtual async Task StopAsync()
    {
        if (_cts == null) return;
        
        _cts.Cancel();
        
        if (_monitoringTask != null)
        {
            try
            {
                await _monitoringTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping clipboard monitor");
            }
        }
        
        _cts?.Dispose();
        _cts = null;
        _monitoringTask = null;
    }

    protected virtual async Task MonitorClipboardLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollingIntervalMs, cancellationToken);
                
                // Read clipboard
                var clipboardText = await _clipboardProvider.GetTextAsync();
                if (string.IsNullOrEmpty(clipboardText))
                    continue;
                
                var currentHash = _utils.ComputeHash(clipboardText);
                if (_lastClipboardHash != currentHash)
                {
                    _lastClipboardHash = currentHash;
                    // Fire event on background thread (don't block UI)
                    OnClipboardChanged?.Invoke(clipboardText);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during clipboard monitoring");
                // Add backoff on error to prevent spam
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    public virtual async Task SetClipboardTextAsync(string text)
    {
        try
        {
            if (!string.IsNullOrEmpty(text))
            {
                _lastClipboardHash = _utils.ComputeHash(text);
            }
            
            await _clipboardProvider.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting clipboard text");
            throw;
        }
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        try
        {
            // Cancel the token first
            _cts?.Cancel();
            
            // Wait for the monitoring task with a timeout to ensure clean shutdown
            if (_monitoringTask != null)
            {
                try
                {
                    _monitoringTask.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                    // Expected if cancellation occurred
                }
            }
            
            _cts?.Dispose();
            _cts = null;
            _monitoringTask = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disposal");
        }
    }
}