using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synclo.Components;
using Synclo.Factory;
using Synclo.Services;
using Synclo.Services.API;
using Synclo.Services.ClipboardMonitor;
using Synclo.Services.ClipboardService;
using Synclo.Services.SecretsManager;
using Synclo.Services.Startup;
using Synclo.Services.Utilities;
using Synclo.Themes;
using Synclo.ViewModels;
using Synclo.Views;

namespace Synclo;

public class App : Application
{
    private MainWindow? _mainWindow;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();

        // View models
        collection.AddSingleton<IViewModelFactory, ViewModelFactory>();
        collection.AddSingleton<MainWindowViewModel>();
        collection.AddSingleton<HomeViewModel>();
        collection.AddTransient<AccountViewModel>();
        collection.AddTransient<SettingsViewModel>();
        collection.AddTransient<LoginViewModel>();
        collection.AddTransient<AccountDetailsViewModel>();
        collection.AddTransient<ResetPasswordDialogViewModel>();

        // API related services
        collection.AddSingleton<HttpClient>();
        collection.AddSingleton<IApiService, ApiService>();
        collection.AddSingleton<IDeviceService, DeviceService>();
        collection.AddSingleton<IRefreshTokenService, RefreshTokenService>();
        collection.AddSingleton<IAccountService, AccountService>();
        collection.AddSingleton<IWebSocketService, WebSocketService>();
        collection.AddSingleton<IClipboardApiService, ClipboardApiService>();

        // Other related services
        collection.AddSingleton<ICryptographyService, CryptographyService>();
        collection.AddSingleton<ISettingsService, SettingsService>();
        collection.AddSingleton<INotificationService, NotificationService>();
        collection.AddSingleton<DialogService.IDialogService, DialogService>();
        collection.AddSingleton<IThemeService, ThemeService>();
        collection.AddSingleton<IApplicationControlService, ApplicationControlService>();
        collection.AddSingleton<IUtils, Utils>();

        // Clipboard subsystem (dependant services)
        collection.AddSingleton<IClipboardRepository, ClipboardRepository>();
        collection.AddSingleton<IClipboardProvider, AvaloniaClipboardProvider>();
        collection.AddSingleton<IClipboardMonitor>(sp =>
        {
            var clipboardProvider = sp.GetRequiredService<IClipboardProvider>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var utils = sp.GetRequiredService<IUtils>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new ClipboardMonitorWindows(clipboardProvider,
                    loggerFactory.CreateLogger<ClipboardMonitorWindows>(), utils);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new ClipboardMonitorMacOS(clipboardProvider, loggerFactory.CreateLogger<ClipboardMonitorMacOS>(),
                    utils);
            return new ClipboardMonitorLinux(clipboardProvider, loggerFactory.CreateLogger<ClipboardMonitorLinux>(),
                utils);
        });
        collection.AddSingleton<IClipboardSyncService, ClipboardSyncService>();
        collection.AddSingleton<IStartupManager>(_ =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new StartupManagerWindows();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new StartupManagerMacOS();
            return new StartupManagerLinux();
        });

        // Startup subsystem
        collection.AddSingleton<ISecureStorage>(_ =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new SecureStorageWindows();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new SecureStorageMacOS();
            return new SecureStorageLinux();
        });

        // Single instance manager (must be registered before building provider)
        collection.AddSingleton<SingleInstanceManager>();

        var services = collection.BuildServiceProvider();

        // ========== SINGLE INSTANCE CHECK (must be first) ==========
        var instanceManager = services.GetRequiredService<SingleInstanceManager>();
        if (!instanceManager.IsPrimary)
        {
            // Signal primary instance to show window and exit
            instanceManager.SignalPrimary();
            Environment.Exit(0);
            return; // Safety: won't reach here
        }

        var apiService = services.GetRequiredService<IApiService>();
        var accountService = services.GetRequiredService<IAccountService>();
        var webSocketService = services.GetRequiredService<IWebSocketService>();
        var settingsService = services.GetRequiredService<ISettingsService>();
        var themeService = services.GetRequiredService<IThemeService>();
        themeService.ApplyTheme(settingsService.Settings.Theme);

        // Wiring events
        var refreshTokenService = services.GetRequiredService<IRefreshTokenService>();
        refreshTokenService.SessionExpired += () => _ = accountService.LogoutAsync();

        // Initialize clipboard subsystem with proper sequencing
        var clipboardRepository = services.GetRequiredService<IClipboardRepository>();
        var clipboardSyncService = services.GetRequiredService<IClipboardSyncService>();
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await clipboardRepository.InitializeAsync();
            await clipboardSyncService.InitializeAsync();
        });

        accountService.OnLogout += webSocketService.DisconnectAsync;
        
        // Handle Single Instance signal
        instanceManager.SignalReceived += () =>
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_mainWindow == null) return;
                
                // Ensure window is visible and active
                if (!_mainWindow.IsVisible)
                {
                    _mainWindow.Show();
                }
                
                if (_mainWindow.WindowState == WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Normal;
                }
                
                _mainWindow.Activate();
                _mainWindow.Topmost = true;
                _mainWindow.Topmost = false;
            });
        };
        
        // Handle Tray Icon Visibility
        settingsService.SettingsChanged += s => 
        {
            Dispatcher.UIThread.InvokeAsync(() => UpdateTrayIconVisibility(s.minimize_to_tray));
        };
        UpdateTrayIconVisibility(settingsService.Settings.minimize_to_tray);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Required for background sync: app runs until explicit shutdown
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            DisableAvaloniaDataAnnotationValidation();

            var mainVm = services.GetRequiredService<MainWindowViewModel>();
            var appControlService = services.GetRequiredService<IApplicationControlService>();

            var mainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            // Wire up close behavior (hide vs exit)
            mainWindow.Initialize(appControlService);

            // ✅ Initialize notification manager BEFORE VM startup
            var notificationService = services.GetRequiredService<INotificationService>();

            var notificationManager = new WindowNotificationManager(mainWindow)
            {
                Position = NotificationPosition.TopRight,
                MaxItems = 3
            };

            notificationService.SetManager(notificationManager);

            _mainWindow = mainWindow;

            // Fix: Ensure clipboard provider uses the correct window even if hidden (autostart)
            if (services.GetService<IClipboardProvider>() is AvaloniaClipboardProvider provider)
            {
                provider.SetHostWindow(mainWindow);
            }

            // Check for autostart
            var isAutostart = desktop.Args?.Contains("--autostart") ?? false;
            
            // If autostart, we determine visibility based on user preference
            if (isAutostart)
            {
                var settings = services.GetRequiredService<ISettingsService>().Settings;
                var startHidden = settings.background_sync_enabled || settings.minimize_to_tray;
                
                if (!startHidden)
                {
                    desktop.MainWindow = mainWindow;
                }
                // Else: Do not set MainWindow, so it starts in "background" mode (Headless)
            }
            else
            {
                // Normal user launch -> Show window
                desktop.MainWindow = mainWindow;
            }
            
            // Ensure VM initialization happens regardless of visibility

            // Now it's safe to initialize the VM
            Dispatcher.UIThread.InvokeAsync(mainVm.InitializeApplicationAsync);

            desktop.Exit += async (_, _) =>
            {
                await clipboardSyncService.ShutdownAsync();
                webSocketService.Dispose();
                apiService.Dispose();
                instanceManager.Dispose();
                mainVm.Dispose();
            };
        }


        base.OnFrameworkInitializationCompleted();
    }


    private void OnExitClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void OnShowHideClicked(object? sender, EventArgs e)
    {
        if (_mainWindow == null) return;

        if (_mainWindow.IsVisible)
        {
            _mainWindow.Hide();
        }
        else
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    private void OnSettingsClicked(object? sender, EventArgs e)
    {
        if (_mainWindow == null) return;

        // Show window if hidden
        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        // Navigate to Settings
        if (_mainWindow.DataContext is MainWindowViewModel vm)
        {
            vm.ShowSettingsCommand.Execute(null);
        }
    }

    private void UpdateTrayIconVisibility(bool isVisible)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons == null) return;

        foreach (var icon in icons)
        {
            icon.IsVisible = isVisible;
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators
                .OfType<DataAnnotationsValidationPlugin>()
                .ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
            BindingPlugins.DataValidators.Remove(plugin);
    }
}