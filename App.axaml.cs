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
    public static DialogService.IDialogService DialogService { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize Services
        Settings = new SettingsService();
        NotificationService = new NotificationService();
        DialogService = new DialogService();
        APIService = new APIService(Settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            var mainVM = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVM
            };
            Dispatcher.UIThread.InvokeAsync(mainVM.InitializeApplicationAsync);
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