using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Synclo.Services.Utilities;

public interface IApplicationControlService
{
    void ShowMainWindow();
    void Shutdown();
    bool ShouldMinimizeOnClose();
    event Action? ShutdownRequested;
}

public sealed class ApplicationControlService(ISettingsService settings) : IApplicationControlService
{
    private int _shutdownRequested;

    public event Action? ShutdownRequested;

    public void ShowMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            var window = desktop.MainWindow;
            if (window == null) return;

            window.WindowState = WindowState.Normal;
            window.Show();

            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
        });
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ShutdownRequested?.Invoke();
            }
            catch
            {
                // Never allow shutdown to fail due to subscribers
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        });
    }

    public bool ShouldMinimizeOnClose()
    {
        return settings.Settings.background_sync_enabled ||
               settings.Settings.minimize_to_tray;
    }
}
