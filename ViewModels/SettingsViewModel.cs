using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Synclo.Services;
using Synclo.Services.ClipboardService;
using Synclo.Services.Startup;
using Synclo.Themes;

namespace Synclo.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IApiService _apiService;
    private readonly IThemeService _themeService;
    private readonly IStartupManager _startupManager;
    
    [ObservableProperty] private string _selectedTheme = "System";
    [ObservableProperty] private bool _showResult;
    
    [ObservableProperty] private MaterialIconKind _healthCheckIcon = MaterialIconKind.QuestionMark;
    [ObservableProperty] private IBrush _healthCheckColor = Brushes.Gray;
    [ObservableProperty] private double _gridOpacity;
    
    [ObservableProperty] private bool _isStartOnBootEnabled;

    public SettingsViewModel(
        ISettingsService settings, 
        IApiService apiService, 
        IThemeService themeService,
        IStartupManager startupManager)
    {
        _settings = settings;
        _apiService = apiService;
        _themeService = themeService;
        _startupManager = startupManager;
        
        SelectedTheme = _settings.Settings.Theme;
        
        // Load start on boot status
        _ = LoadStartOnBootStatusAsync();
    }

    private async Task LoadStartOnBootStatusAsync()
    {
        try
        {
            IsStartOnBootEnabled = await _startupManager.IsEnabledAsync();
        }
        catch
        {
            IsStartOnBootEnabled = false;
        }
    }

    public List<string> AvailableThemes { get; } = new() { "System", "Light", "Dark" };

    partial void OnSelectedThemeChanged(string value)
    {
        _themeService.ApplyTheme(value);
        _settings.Settings.Theme = value;
        _settings.Save();
    }



    partial void OnIsStartOnBootEnabledChanged(bool value)
    {
        _settings.Settings.start_on_boot = value;
        _settings.Save();
        
        // Configure autostart
        _ = Task.Run(async () =>
        {
            try
            {
                if (value)
                {
                    await _startupManager.EnableAsync();
                }
                else
                {
                    await _startupManager.DisableAsync();
                }
            }
            catch (Exception ex)
            {
                // Revert the toggle state on UI thread and notify user
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Use the backing field to avoid re-triggering OnChanged
                    _isStartOnBootEnabled = !value;
                    OnPropertyChanged(nameof(IsStartOnBootEnabled));
                    _settings.Settings.start_on_boot = !value;
                    _settings.Save();
                });
                
                // Show notification - NotificationService is typically thread-safe
                System.Diagnostics.Debug.WriteLine($"Failed to configure autostart: {ex.Message}");
            }
        });
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