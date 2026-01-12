using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Synclo.Services.ClipboardService;

/// <summary>
/// Windows implementation of clipboard monitor using polling.
/// Polls clipboard every 500ms and detects changes via content hashing.
/// </summary>
public class ClipboardMonitorWindows : IClipboardMonitor, IDisposable
{
    private const int PollingIntervalMs = 500;
    private CancellationTokenSource? _cts;
    private Task? _monitoringTask;
    private string? _lastClipboardHash;
    private bool _disposed;

    public event Action<string>? OnClipboardChanged;
    public bool IsRunning => _cts != null && !_cts.Token.IsCancellationRequested;

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;

        _cts = new CancellationTokenSource();
        
        // Initialize hash on startup to avoid immediately firing for pre-existing content
        _monitoringTask = Task.Run(async () =>
        {
            // Read current clipboard and set initial hash before monitoring loop
            await InitializeClipboardHashAsync();
            await MonitorClipboardLoop(_cts.Token);
        });
        
        return Task.CompletedTask;
    }

    private async Task InitializeClipboardHashAsync()
    {
        try
        {
            var clipboardText = await Dispatcher.UIThread.InvokeAsync(async () =>
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

            if (!string.IsNullOrEmpty(clipboardText))
            {
                _lastClipboardHash = ComputeHash(clipboardText);
            }
        }
        catch
        {
            // Ignore errors during initialization
        }
    }

    public async Task StopAsync()
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
        }

        _cts?.Dispose();
        _cts = null;
        _monitoringTask = null;
    }

    private async Task MonitorClipboardLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollingIntervalMs, cancellationToken);

                // Read clipboard on UI thread (Avalonia requirement)
                var clipboardText = await Dispatcher.UIThread.InvokeAsync(async () =>
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

                if (string.IsNullOrEmpty(clipboardText))
                    continue;

                // Hash content to detect changes
                var currentHash = ComputeHash(clipboardText);
                
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
            catch (Exception)
            {
                // Ignore errors and continue monitoring
            }
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    public async Task SetClipboardTextAsync(string text)
    {
        // Update hash BEFORE setting clipboard to prevent echo detection
        if (!string.IsNullOrEmpty(text))
        {
            _lastClipboardHash = ComputeHash(text);
        }
        
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
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
}
