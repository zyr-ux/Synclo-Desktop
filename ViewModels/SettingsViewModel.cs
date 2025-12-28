using System.Collections.Generic;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Services;

namespace Synclo.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    [ObservableProperty] private string _selectedTheme = "System";

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
    private void HealthCheck()
    {
    }
}