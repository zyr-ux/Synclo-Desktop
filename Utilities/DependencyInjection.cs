using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synclo.Themes;
using Synclo.ViewModels;
using Synclo.Features.Clipboard_Manager.Clipboard_Monitor;
using Synclo.Features.Clipboard_Manager.Clipboard_Service;
using Synclo.Features.Dialog_Manager;
using Synclo.Features.Dialog_Manager.Confirmation_Dialog;
using Synclo.Features.Dialog_Manager.Reset_Password_Dialog;
using Synclo.Features.Connection_Monitor;
using Synclo.Features.Network_Services;
using Synclo.Features.Notifications_Manager;
using Synclo.Features.Secrets_Manager;
using Synclo.Features.Settings_Manager;
using Synclo.Features.Startup_Manager;

namespace Synclo.Utilities;

public static class DependencyInjection
{
    public static IServiceCollection AddSyncloServices(this IServiceCollection services)
    {
        services.AddLogging();

        // View models
        services.AddSingleton<IViewModelFactory, ViewModelFactory>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddTransient<AccountViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<AccountDetailsViewModel>();
        services.AddTransient<AboutViewModel>();

        // API related services
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IApiService, ApiService>();
        services.AddSingleton<IDeviceService, DeviceService>();
        services.AddSingleton<IRefreshTokenService, RefreshTokenService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IWebSocketService, WebSocketService>();
        services.AddSingleton<IClipboardApiService, ClipboardApiService>();

        // Other related services
        services.AddSingleton<ICryptographyService, CryptographyService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IApplicationControlService, ApplicationControlService>();
        services.AddSingleton<IUtils, Utils>();
        services.AddSingleton<IConnectionMonitor, ConnectionMonitor>();

        // Clipboard subsystem (dependent services)
        services.AddSingleton<IClipboardRepository, ClipboardRepository>();
        services.AddSingleton<IClipboardProvider, AvaloniaClipboardProvider>();
        services.AddSingleton<IClipboardMonitor>(sp =>
        {
            var clipboardProvider = sp.GetRequiredService<IClipboardProvider>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var utils = sp.GetRequiredService<IUtils>();
            
            if (OperatingSystem.IsWindows())
                return new ClipboardMonitorWindows(clipboardProvider,
                    loggerFactory.CreateLogger<ClipboardMonitorWindows>(), utils);
            if (OperatingSystem.IsMacOS())
                return new ClipboardMonitorMacOS(clipboardProvider,
                    loggerFactory.CreateLogger<ClipboardMonitorMacOS>(), utils);
            
            return new ClipboardMonitorLinux(clipboardProvider,
                loggerFactory.CreateLogger<ClipboardMonitorLinux>(), utils);
        });
        services.AddSingleton<IClipboardSyncService, ClipboardSyncService>();
        
        services.AddSingleton<IStartupManager>(_ =>
        {
            if (OperatingSystem.IsWindows())
                return new StartupManagerWindows();
            if (OperatingSystem.IsMacOS())
                return new StartupManagerMacOS();
            return new StartupManagerLinux();
        });

        // Startup subsystem
        services.AddSingleton<ISecureStorage>(_ =>
        {
            if (OperatingSystem.IsWindows())
                return new SecureStorageWindows();
            if (OperatingSystem.IsMacOS())
                return new SecureStorageMacOS();
            if (OperatingSystem.IsLinux())
                return new SecureStorageLinux();
            throw new PlatformNotSupportedException("Unsupported operating system");
        });

        services.AddSingleton<ISecretsManager, Synclo.Features.Secrets_Manager.SecretsManager>();

        // Single instance manager
        services.AddSingleton<SingleInstanceManager>();

        return services;
    }
}
