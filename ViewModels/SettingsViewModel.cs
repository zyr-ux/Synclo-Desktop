using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Synclo.Services;
using Synclo.Themes;

namespace Synclo.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly APIService _apiService;
    private readonly IThemeService _themeService;
    [ObservableProperty] private string _selectedTheme = "System";
    [ObservableProperty] private bool _showResult;
    
    [ObservableProperty] private MaterialIconKind _healthCheckIcon = MaterialIconKind.QuestionMark;
    [ObservableProperty] private IBrush _healthCheckColor = Brushes.Gray;
    [ObservableProperty] private double _gridOpacity;

    public SettingsViewModel(ISettingsService settings, APIService apiService, IThemeService themeService)
    {
        _settings = settings;
        _apiService = apiService;
        _themeService = themeService;
        SelectedTheme = _settings.Settings.Theme;
    }

    public List<string> AvailableThemes { get; } = new() { "System", "Light", "Dark" };

    partial void OnSelectedThemeChanged(string value)
    {
        _themeService.ApplyTheme(value);
        _settings.Settings.Theme = value;
        _settings.Save();
    }

    [RelayCommand]
    private async Task HealthCheck()
    {
        GridOpacity = 1.0; 
    
        try
        {
            await _apiService.Health().WaitAsync(TimeSpan.FromSeconds(0.5));
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