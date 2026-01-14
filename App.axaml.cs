using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synclo.Components;
using Synclo.Factory;
using Synclo.SecretsManager;
using Synclo.Services;
using Synclo.Services.ClipboardService;
using Synclo.Services.ClipboardService.ClipboardMonitoringService;
using Synclo.Services.Startup;
using Synclo.Themes;
using Synclo.ViewModels;
using Synclo.Views;

namespace Synclo;

public class App : Application
{
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
        collection.AddSingleton<APIService>();
        collection.AddSingleton<DeviceService>();
        collection.AddSingleton<IRefreshTokenService, RefreshTokenService>();
        collection.AddSingleton<AccountService>();
        collection.AddSingleton<WebSocketService>();
        collection.AddSingleton(new ApiConfig
        {
            ApiTimeoutSeconds = 30
        });
        collection.AddSingleton<IClipboardApiService, ClipboardApiService>();
        
        // Clipboard subsystem
        collection.AddSingleton<IClipboardRepository, ClipboardRepository>();
        collection.AddSingleton<IClipboardProvider, AvaloniaClipboardProvider>();
        collection.AddSingleton<IClipboardMonitor>(sp => 
        {
            var clipboardProvider = sp.GetRequiredService<IClipboardProvider>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new ClipboardMonitorWindows(clipboardProvider, loggerFactory.CreateLogger<ClipboardMonitorWindows>());
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new ClipboardMonitorMacOS(clipboardProvider, loggerFactory.CreateLogger<ClipboardMonitorMacOS>());
            return new ClipboardMonitorLinux(clipboardProvider, loggerFactory.CreateLogger<ClipboardMonitorLinux>());
        });
        collection.AddSingleton<ClipboardSyncService>();
        collection.AddSingleton(new RepositoryConfig
        {
            DefaultHistoryLimit = 100
        });
        collection.AddSingleton(new ClipboardSyncConfig
        {
            DebounceDelayMs = 500,
            InactivityThresholdDays = 14,
            DefaultHistoryLimit = 100,
            DefaultSyncPageSize = 50,
            ShutdownTimeoutSeconds = 5,
            MinRefreshIntervalMs = 300,
            MaxRetryAttempts = 3,
            BaseRetryDelayMs = 1000,
            RetryProcessorTimeoutMs = 5000
        });
        collection.AddSingleton<IStartupManager>(_ => {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new StartupManagerWindows();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new StartupManagerMacOS();
            return new StartupManagerLinux();
        });
        
        // Other related services
        collection.AddSingleton<CryptographyService>();
        collection.AddSingleton<ISettingsService, SettingsService>();
        collection.AddSingleton<NotificationService>();
        collection.AddSingleton<DialogService.IDialogService, DialogService>();
        collection.AddSingleton<IThemeService, ThemeService>();
        collection.AddSingleton<Utils>();
        
        // Startup subsystem
        collection.AddSingleton<ISecureStorage>(_ => {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new SecureStorageWindows();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new SecureStorageMacOS();
            return new SecureStorageLinux();
        });
        
        var services = collection.BuildServiceProvider();

        var apiService = services.GetRequiredService<APIService>();
        var refreshTokenService = services.GetRequiredService<IRefreshTokenService>();
        var accountService = services.GetRequiredService<AccountService>();
        var webSocketService = services.GetRequiredService<WebSocketService>();
        var settingsService = services.GetRequiredService<ISettingsService>();
        var themeService = services.GetRequiredService<IThemeService>();
        themeService.ApplyTheme(settingsService.Settings.Theme);
        
        // Initialize clipboard subsystem with proper sequencing
        var clipboardRepository = services.GetRequiredService<IClipboardRepository>();
        var clipboardSyncService = services.GetRequiredService<ClipboardSyncService>();
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await clipboardRepository.InitializeAsync();
            await clipboardSyncService.InitializeAsync();
        });
        
        // Event handlers
        apiService.OnTokenExpired += refreshTokenService.RefreshAsync;
        accountService.OnLogout += webSocketService.DisconnectAsync;
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
{
    DisableAvaloniaDataAnnotationValidation();

    var mainVM = services.GetRequiredService<MainWindowViewModel>();

    var mainWindow = new MainWindow
    {
        DataContext = mainVM
    };

    // ✅ Initialize notification manager BEFORE VM startup
    var notificationService = services.GetRequiredService<NotificationService>();

    var notificationManager = new WindowNotificationManager(mainWindow)
    {
        Position = NotificationPosition.TopRight,
        MaxItems = 3
    };

    notificationService.SetManager(notificationManager);

    desktop.MainWindow = mainWindow;

    // ✅ Now it's safe to initialize the VM
    Dispatcher.UIThread.InvokeAsync(mainVM.InitializeApplicationAsync);

    desktop.Exit += async (_, _) =>
    {
        await clipboardSyncService.ShutdownAsync();
        webSocketService.Dispose();
        apiService.Dispose();
        mainVM.Dispose();
    };
}


        base.OnFrameworkInitializationCompleted();
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