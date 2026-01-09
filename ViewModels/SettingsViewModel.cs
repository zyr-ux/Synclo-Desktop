using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Synclo.Services;

namespace Synclo.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    [ObservableProperty] private string _selectedTheme = "System";
    [ObservableProperty] private bool _showResult;
    
    [ObservableProperty] private MaterialIconKind _healthCheckIcon = MaterialIconKind.QuestionMark;
    [ObservableProperty] private IBrush _healthCheckColor = Brushes.Gray;
    [ObservableProperty] private double _gridOpacity;

    public SettingsViewModel()
    {
        _settings = App.Settings;
        SelectedTheme = _settings.Settings.Theme;
    }

    public List<string> AvailableThemes { get; } = new() { "System", "Light", "Dark" };

    // Theme changing code
    partial void OnSelectedThemeChanged(string value)
    {
        var app = Application.Current!;
        switch (value)
        {
            case "Light":
                app.RequestedThemeVariant = ThemeVariant.Light;
                break;

            case "Dark":
                app.RequestedThemeVariant = ThemeVariant.Dark;
                break;

            default:
                app.RequestedThemeVariant = ThemeVariant.Default;
                break;
        }

        _settings.Settings.Theme = value;
        _settings.Save();
    }

    [RelayCommand]
    private async Task HealthCheck()
    {
        GridOpacity = 1.0; 
    
        try
        {
            await App.APIService.Health().WaitAsync(TimeSpan.FromSeconds(1));
            HealthCheckIcon = MaterialIconKind.Check;
            HealthCheckColor = Brushes.LimeGreen;
        }
        catch
        {
            HealthCheckIcon = MaterialIconKind.Close;
            HealthCheckColor = Brushes.Red;
        }
        finally
        {
            await Task.Delay(1000); 
            GridOpacity = 0.0; 
        }
    }
}