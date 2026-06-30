using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Synclo.Features.Network_Services;
using Synclo.Features.Clipboard_Manager.Clipboard_Monitor;
using Synclo.Features.Clipboard_Manager.Clipboard_Service;
using Synclo.Features.Secrets_Manager;
using Synclo.Utilities;
using Synclo.Features.Settings_Manager;
using Synclo.Features.Notifications_Manager;
using Synclo.Features.Connection_Monitor;
using Synclo.Features.Dialog_Manager;
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
        // Setup DI
        var collection = new ServiceCollection();
        collection.AddSyncloServices();
        _services = collection.BuildServiceProvider();

        var instanceManager = _services.GetRequiredService<SingleInstanceManager>();

        // Single Instance Check
        if (!CheckSingleInstance(instanceManager))
        {
            Environment.Exit(0);
            return;
        }

        // Resolve key system services
        _appControl = _services.GetRequiredService<IApplicationControlService>();
        var settingsService = _services.GetRequiredService<ISettingsService>();
        var themeService = _services.GetRequiredService<IThemeService>();

        // Apply visual theme
        themeService.ApplyTheme(settingsService.Settings.Theme);

        // Handle Tray Icon Visibility
        settingsService.SettingsChanged += s =>
        {
            Dispatcher.UIThread.InvokeAsync(() => UpdateTrayIconVisibility(s.minimize_to_tray));
        };
        UpdateTrayIconVisibility(settingsService.Settings.minimize_to_tray);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Sequential async initialization
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                System.Security.SecurityException? kdfException = null;
                try
                {
                    await InitializeServicesAsync(_services);
                }
                catch (System.Security.SecurityException ex)
                {
                    kdfException = ex;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during services initialization: {ex}");
                }

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

                _mainWindow.Show();

                if (kdfException != null)
                {
                    notificationService.ShowError(kdfException.Message, "Security Update");
                }
            });

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
                        await ShutdownServicesAsync(_services);
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

    private bool CheckSingleInstance(SingleInstanceManager instanceManager)
    {
        if (!instanceManager.IsPrimary)
        {
            instanceManager.SignalPrimary();
            return false;
        }
        return true;
    }

    private async Task InitializeServicesAsync(IServiceProvider services)
    {
        var appControl = services.GetRequiredService<IApplicationControlService>();
        var secretsManager = services.GetRequiredService<ISecretsManager>();
        var settingsService = services.GetRequiredService<ISettingsService>();
        var accountService = services.GetRequiredService<IAccountService>();
        var webSocketService = services.GetRequiredService<IWebSocketService>();
        var refreshTokenService = services.GetRequiredService<IRefreshTokenService>();
        var instanceManager = services.GetRequiredService<SingleInstanceManager>();

        // IPC single instance focus event
        instanceManager.SignalReceived += () => appControl.ShowMainWindow();

        // Wire up event connections
        refreshTokenService.SessionExpired += OnSessionExpired;
        accountService.OnLogout += webSocketService.DisconnectAsync;
        accountService.OnLoggedOutRemotely += () =>
        {
            var notificationService = services.GetRequiredService<INotificationService>();
            Dispatcher.UIThread.Post(() => 
                notificationService.ShowWarning("This device has been logged out remotely"));
        };

        // 1. Restore ServerUrl from secure secrets manager
        var serverUrl = await secretsManager.GetServerUrlAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(serverUrl))
        {
            settingsService.Settings.ServerUrl = serverUrl;
        }

        // 2. Enforce local KDF version check (will throw SecurityException if invalid)
        await accountService.EnforceLocalKdfVersionAsync().ConfigureAwait(false);

        // 3. Initialize repository
        var clipboardRepository = services.GetRequiredService<IClipboardRepository>();
        await clipboardRepository.InitializeAsync().ConfigureAwait(false);

        // 4. Initialize clipboard sync service (which connects WebSocket and syncs)
        var clipboardSyncService = services.GetRequiredService<IClipboardSyncService>();
        await clipboardSyncService.InitializeAsync().ConfigureAwait(false);

        // 5. Warm up connection monitor to start background polling
        _ = services.GetRequiredService<IConnectionMonitor>();
    }

    private void OnSessionExpired()
    {
        if (_services == null) return;
        var accountService = _services.GetRequiredService<IAccountService>();
        _ = accountService.LogoutAsync();
    }

    private async Task ShutdownServicesAsync(IServiceProvider services)
    {
        // Graceful cleanup of registered services
        var clipboardSyncService = services.GetRequiredService<IClipboardSyncService>();
        await clipboardSyncService.ShutdownAsync();

        var webSocketService = services.GetRequiredService<IWebSocketService>();
        webSocketService.Dispose();

        var apiService = services.GetRequiredService<IApiService>();
        apiService.Dispose();

        var instanceManager = services.GetRequiredService<SingleInstanceManager>();
        instanceManager.Dispose();

        var connectionMonitor = services.GetService<IConnectionMonitor>();
        connectionMonitor?.Dispose();

        var mainVm = services.GetRequiredService<MainWindowViewModel>();
        mainVm.Dispose();
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