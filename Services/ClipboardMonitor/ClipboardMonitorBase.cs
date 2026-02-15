using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Synclo.Services.Utilities;

namespace Synclo.Services.ClipboardMonitor;

public interface IClipboardProvider
{
    Task<string?> GetTextAsync();
    Task SetTextAsync(string text);
}

public interface ISynchronousClipboardProvider
{
    string? GetText();
    void SetText(string text);
}

public class AvaloniaClipboardProvider : IClipboardProvider, ISynchronousClipboardProvider
{
    private Window? _hostWindow;

    public void SetHostWindow(Window window)
    {
        _hostWindow = window;
    }

    private IClipboard? GetClipboard(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_hostWindow?.Clipboard is { } hostClipboard)
            return hostClipboard;

        if (desktop.MainWindow?.Clipboard is { } mainClipboard)
            return mainClipboard;

        foreach (var window in desktop.Windows)
        {
            if (window.Clipboard is { } clipboard)
                return clipboard;
        }

        return null;
    }

    public async Task<string?> GetTextAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var clipboard = GetClipboard(desktop);
            if (clipboard is null)
                return null;

            return await clipboard.TryGetTextAsync();
        });
    }

    public async Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var clipboard = GetClipboard(desktop);
            if (clipboard is null)
                return;

            await clipboard.SetTextAsync(text);
        });
    }

    public string? GetText()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        if (Dispatcher.UIThread.CheckAccess())
        {
            var clipboard = GetClipboard(desktop);
            return clipboard?.TryGetTextAsync().GetAwaiter().GetResult();
        }

        return Dispatcher.UIThread
            .InvokeAsync(async () =>
            {
                var clipboard = GetClipboard(desktop);
                if (clipboard is null)
                    return null;

                return await clipboard.TryGetTextAsync();
            })
            .GetAwaiter()
            .GetResult();
    }

    public void SetText(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            var clipboard = GetClipboard(desktop);
            clipboard?.SetTextAsync(text).GetAwaiter().GetResult();
            return;
        }

        Dispatcher.UIThread
            .InvokeAsync(async () =>
            {
                var clipboard = GetClipboard(desktop);
                if (clipboard is null)
                    return;

                await clipboard.SetTextAsync(text);
            })
            .GetAwaiter()
            .GetResult();
    }
}

public abstract class ClipboardMonitorBase(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorBase> logger,
    IUtils utils,
    int pollingIntervalMs = ClipboardMonitorBase.DefaultPollingIntervalMs)
    : IClipboardMonitor, IDisposable
{
    private const int DefaultPollingIntervalMs = 500;

    private readonly IClipboardProvider _clipboardProvider =
        clipboardProvider ?? throw new ArgumentNullException(nameof(clipboardProvider));

    private readonly ILogger<ClipboardMonitorBase> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IUtils _utils =
        utils ?? throw new ArgumentNullException(nameof(utils));

    private CancellationTokenSource? _cts;
    private Task? _monitoringTask;
    private bool _disposed;
    private string? _lastClipboardHash;

    public event Action<string>? OnClipboardChanged;

    public bool IsRunning =>
        _monitoringTask is { IsCompleted: false } &&
        _cts is { IsCancellationRequested: false };

    public virtual async Task StartAsync()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();

        await InitializeClipboardHashAsync();

        _monitoringTask = Task.Run(
            () => MonitorClipboardLoop(_cts.Token),
            _cts.Token);
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping clipboard monitor");
            }
        }

        _cts.Dispose();
        _cts = null;
        _monitoringTask = null;
    }

    public virtual async Task SetClipboardTextAsync(string text)
    {
        try
        {
            await _clipboardProvider.SetTextAsync(text);
            _lastClipboardHash = _utils.ComputeHash(text);
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
            var cts = _cts;
            var monitoringTask = _monitoringTask;

            _cts = null;
            _monitoringTask = null;

            if (cts is null)
                return;

            cts.Cancel();

            if (monitoringTask is null)
            {
                cts.Dispose();
                return;
            }

            _ = monitoringTask.ContinueWith(
                _ => cts.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disposal");
        }
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

    protected virtual async Task MonitorClipboardLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollingIntervalMs, cancellationToken);

                var clipboardText = await _clipboardProvider.GetTextAsync();
                if (string.IsNullOrEmpty(clipboardText))
                    continue;

                var currentHash = _utils.ComputeHash(clipboardText);
                if (_lastClipboardHash == currentHash)
                    continue;

                _lastClipboardHash = currentHash;

                var text = clipboardText;
                Task.Run(() => OnClipboardChanged?.Invoke(text));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during clipboard monitoring");
                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
