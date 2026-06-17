using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Synclo.Services.ClipboardMonitor;
using Synclo.Services.Utilities;
using Synclo.Themes;
using Synclo.ViewModels;
using Synclo.Views;

namespace Synclo;

public class App : Application
{
    private IApplicationControlService? _appControl;
    private MainWindow? _mainWindow;
    private IServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 1. Setup DI
        var collection = new ServiceCollection();
        collection.AddSyncloServices();
        _services = collection.BuildServiceProvider();

        // 2. Single Instance Check
        var instanceManager = _services.GetRequiredService<SingleInstanceManager>();
        if (!instanceManager.IsPrimary)
        {
            instanceManager.SignalPrimary();
            Environment.Exit(0);
            return;
        }

        // 3. Resolve key system services
        _appControl = _services.GetRequiredService<IApplicationControlService>();
        var settingsService = _services.GetRequiredService<ISettingsService>();
        var themeService = _services.GetRequiredService<IThemeService>();

        themeService.ApplyTheme(settingsService.Settings.Theme);

        // 4. Initialize Bootstrapper (wires ws connection, clipboard repository, etc)
        var bootstrapper = _services.GetRequiredService<IAppBootstrapper>();
        bootstrapper.Initialize(_services);

        // 5. IPC single instance focus event
        instanceManager.SignalReceived += () => _appControl.ShowMainWindow();

        // 6. Handle Tray Icon Visibility
        settingsService.SettingsChanged += s =>
        {
            Dispatcher.UIThread.InvokeAsync(() => UpdateTrayIconVisibility(s.minimize_to_tray));
        };
        UpdateTrayIconVisibility(settingsService.Settings.minimize_to_tray);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainVm = _services.GetRequiredService<MainWindowViewModel>();
            _mainWindow = new MainWindow { DataContext = mainVm };
            themeService.ApplyMica(settingsService.Settings.is_mica_enabled, _mainWindow);
            _mainWindow.Initialize(_appControl);
            _appControl.SetWindow(_mainWindow);

            // Setup notification manager
            var notificationService = _services.GetRequiredService<INotificationService>();
            var notificationManager = new WindowNotificationManager(_mainWindow)
            {
                Position = NotificationPosition.TopRight,
                MaxItems = 3,
                Margin = new Thickness(0, 40, 0, 0)
            };
            notificationService.SetManager(notificationManager);

            // Set host window on ClipboardProvider
            if (_services.GetService<IClipboardProvider>() is AvaloniaClipboardProvider provider)
                provider.SetHostWindow(_mainWindow);

            // Determine autostart hidden window states
            var isAutostart = desktop.Args?.Contains("--autostart") ?? false;
            if (isAutostart)
            {
                var startHidden = settingsService.Settings.background_sync_enabled ||
                                  settingsService.Settings.minimize_to_tray;
                if (!startHidden) desktop.MainWindow = _mainWindow;
            }
            else
            {
                desktop.MainWindow = _mainWindow;
            }

            // Initialize UI View Model
            Dispatcher.UIThread.InvokeAsync(mainVm.InitializeApplicationAsync);

            // Graceful shutdown wiring
            var isShuttingDown = false;
            desktop.ShutdownRequested += (s, e) =>
            {
                if (isShuttingDown) return;
                e.Cancel = true;
                isShuttingDown = true;

                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        await bootstrapper.ShutdownAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error during shutdown: {ex}");
                    }
                    finally
                    {
                        desktop.Shutdown();
                    }
                });
            };

            base.OnFrameworkInitializationCompleted();
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _appControl?.Shutdown();
    }

    private void OnShowHideClicked(object? sender, EventArgs e)
    {
        _appControl?.ToggleMainWindow();
    }

    private void OnSettingsClicked(object? sender, EventArgs e)
    {
        _appControl?.ShowSettings();
    }

    private void UpdateTrayIconVisibility(bool isVisible)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons == null) return;

        foreach (var icon in icons) icon.IsVisible = isVisible;
    }
}