using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Synclo.ViewModels;

namespace Synclo.Services.Utilities;

public interface IApplicationControlService
{
    void SetWindow(Window window);
    void ShowMainWindow();
    void ToggleMainWindow();
    void ShowSettings();
    void Shutdown();
    bool ShouldMinimizeOnClose();
    event Action? ShutdownRequested;
}

public sealed class ApplicationControlService(ISettingsService settings) : IApplicationControlService
{
    private Window? _window;
    private int _shutdownRequested;

    public event Action? ShutdownRequested;

    public void SetWindow(Window window)
    {
        _window = window;
    }

    public void ShowMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_window == null)
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    _window = desktop.MainWindow;
                }
            }

            if (_window == null) return;

            _window.WindowState = WindowState.Normal;
            _window.Show();

            _window.Topmost = true;
            _window.Activate();
            _window.Topmost = false;
        });
    }

    public void ToggleMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_window == null)
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    _window = desktop.MainWindow;
                }
            }

            if (_window == null) return;

            if (_window.IsVisible)
            {
                _window.Hide();
            }
            else
            {
                ShowMainWindow();
            }
        });
    }

    public void ShowSettings()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_window == null)
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    _window = desktop.MainWindow;
                }
            }

            if (_window == null) return;

            // Show window if hidden
            if (!_window.IsVisible)
            {
                ShowMainWindow();
            }
            else
            {
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            }

            // Navigate to Settings
            if (_window.DataContext is MainWindowViewModel vm)
            {
                vm.ShowSettingsCommand.Execute(null);
            }
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
