using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Synclo.Services;
using Synclo.ViewModels;
using Synclo.Views;

namespace Synclo;

public class App : Application
{
    public static ISettingsService Settings { get; private set; }
    public static APIService APIService { get; private set; }
    public static NotificationService NotificationService { get; private set; }
    
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = new SettingsService();
        NotificationService = new NotificationService();
        APIService = new APIService(Settings);
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            var mainVM = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVM
            };
            
            desktop.Exit += (s, e) =>
            {
                APIService?.Dispose();
                NotificationService = null;
                mainVM.Dispose();
            };
            
            _ = Task.Run(async () => 
            {
                await Task.Delay(100);
                if (await APIService.AccountService.IsAuthenticatedAsync())
                {
                    await APIService.WebSocketService.ConnectAsync();
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove) BindingPlugins.DataValidators.Remove(plugin);
    }
}