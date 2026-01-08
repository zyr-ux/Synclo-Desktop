using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
        // 1. Initialize Services
        Settings = new SettingsService();
        NotificationService = new NotificationService();
        APIService = new APIService(Settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            // 2. Create the Main ViewModel
            var mainVM = new MainWindowViewModel();

            // 3. Create the Main Window
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVM
            };

            // 4. Trigger Secure Startup Sequence (Safe on UI Thread)
            // We use Dispatcher to ensure the Window is fully constructed first.
            Dispatcher.UIThread.InvokeAsync(mainVM.InitializeApplicationAsync);

            // 5. Cleanup on Exit
            desktop.Exit += (s, e) =>
            {
                APIService?.Dispose();
                NotificationService = null;
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