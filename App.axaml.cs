using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Synclo.Components;
using Synclo.Factory;
using Synclo.SecretsManager;
using Synclo.Services;
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
        collection.AddSingleton<WebSocketService>();
        collection.AddSingleton<AccountService>();
        collection.AddSingleton<ClipboardService>();
        
        // Other related services
        collection.AddTransient<CryptographyService>();
        collection.AddSingleton<ISettingsService, SettingsService>();
        collection.AddSingleton<NotificationService>();
        collection.AddSingleton<DialogService.IDialogService, DialogService>();
        collection.AddSingleton<IThemeService, ThemeService>();
        collection.AddSingleton<Utils>();
        collection.AddSingleton<ISecureStorage>(_ => {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new SecureStorageWindows();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new SecureStorageMacOS();
            return new SecureStorageLinux();
        });
        
        var services = collection.BuildServiceProvider();
        var apiService = services.GetRequiredService<APIService>();
        var accountService = services.GetRequiredService<AccountService>();
        apiService.SetRefreshTokenFunc(accountService.RefreshTokenAsync);
        var webSocketService = services.GetRequiredService<WebSocketService>();
        var settingsService = services.GetRequiredService<ISettingsService>();
        var themeService = services.GetRequiredService<IThemeService>();
        themeService.ApplyTheme(settingsService.Settings.Theme);
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            var mainVM = services.GetRequiredService<MainWindowViewModel>();
            var notificationService = services.GetRequiredService<NotificationService>();
            desktop.MainWindow = new MainWindow(notificationService)
            {
                DataContext = mainVM
            };
            Dispatcher.UIThread.InvokeAsync(mainVM.InitializeApplicationAsync);
            desktop.Exit += (_, _) =>
            {
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