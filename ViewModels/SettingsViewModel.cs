using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Synclo.Models;
using Synclo.Services;
using Synclo.Services.API;
using Synclo.Services.ClipboardService;
using Synclo.Services.Startup;
using Synclo.Services.Utilities;
using Synclo.Services.SecretsManager;
using Synclo.Themes;

namespace Synclo.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IApiService _apiService;
    private readonly IThemeService _themeService;
    private readonly IStartupManager _startupManager;
    private readonly IAccountService _accountService;
    private readonly INotificationService _notificationService;
    private readonly ISecretsManager _secretsManager;
    private readonly IUtils _utils;

    [ObservableProperty] private string _selectedTheme = "System";
    [ObservableProperty] private bool _showResult;
    [ObservableProperty] private bool _isStartOnBootEnabled;
    [ObservableProperty] private bool _isMicaEnabled;
    [ObservableProperty] private string _serverUrl = "";
    
    private string _previousServerUrl = "";
    private bool _isUpdatingServerUrl;
    
    public bool IsMicaToggleVisible => OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 22000;
    
    public List<string> AvailableThemes { get; } = ["System", "Light", "Dark"];
    
    public List<string> AvailableCloseBehaviors { get; } = ["Quit Application", "Minimize to System Tray", "Run in Background (Hidden)"];
    
    [ObservableProperty] private string _selectedCloseBehavior="";

    public SettingsViewModel(
        ISettingsService settings, 
        IApiService apiService, 
        IThemeService themeService,
        IStartupManager startupManager,
        IAccountService accountService,
        INotificationService notificationService,
        ISecretsManager secretsManager,
        IUtils utils)
    {
        _settings = settings;
        _apiService = apiService;
        _themeService = themeService;
        _startupManager = startupManager;
        _accountService = accountService;
        _notificationService = notificationService;
        _secretsManager = secretsManager;
        _utils = utils;
        
        SelectedTheme = _settings.Settings.Theme;
        IsMicaEnabled = _settings.Settings.is_mica_enabled;
        ServerUrl = _settings.Settings.ServerUrl;
        _previousServerUrl = ServerUrl;
        
        // Load start on boot status
        _ = LoadStartOnBootStatusAsync();
        
        // Load other settings
        InitializeCloseBehavior();
    }

    partial void OnIsMicaEnabledChanged(bool value)
    {
        _settings.Settings.is_mica_enabled = value;
        _settings.Save();
        _themeService.ApplyMica(value);
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
    
    partial void OnSelectedThemeChanged(string value)
    {
        _themeService.ApplyTheme(value);
        _themeService.ApplyMica(_isMicaEnabled);
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
    
    public enum CloseBehavior // Public for View binding
    {
        Quit,
        MinimizeToTray,
        RunInBackground
    }
    
    partial void OnSelectedCloseBehaviorChanged(string value)
    {
        switch (value)
        {
            case "Quit Application":
                _settings.Settings.background_sync_enabled = false;
                _settings.Settings.minimize_to_tray = false;
                break;
            case "Minimize to System Tray":
                _settings.Settings.background_sync_enabled = true;
                _settings.Settings.minimize_to_tray = true;
                break;
            case "Run in Background (Hidden)":
                _settings.Settings.background_sync_enabled = true;
                _settings.Settings.minimize_to_tray = false;
                break;
        }

        _settings.Save();
    }
    
    private void InitializeCloseBehavior()
    {
        // Settings -> UI
        bool sync = _settings.Settings.background_sync_enabled;
        bool tray = _settings.Settings.minimize_to_tray;
        
        if (sync && tray)
        {
            SelectedCloseBehavior = "Minimize to System Tray";
        }
        else if (sync && !tray)
        {
            SelectedCloseBehavior = "Run in Background (Hidden)";
        }
        else
        {
            SelectedCloseBehavior = "Quit Application";
        }
    }

    public async Task UpdateServerUrlAsync(string? text)
    {
        if (_isUpdatingServerUrl) 
            return;
        
        _isUpdatingServerUrl = true;

        try
        {
            if (!_utils.TryNormalizeServerUrl(text, out var cleanUrl, out var error))
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _notificationService.ShowError(error!);
                    OnPropertyChanged(nameof(ServerUrl));
                });
                return;
            }

            if (cleanUrl == _previousServerUrl)
                return;

            // Ping the candidate URL before committing it (skip for the default server)
            if (cleanUrl != AppSettings.DefaultServerUrl)
            {
                try
                {
                    await _apiService.Health(cleanUrl);
                }
                catch
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _notificationService.ShowError("Could not connect to server.");
                        OnPropertyChanged(nameof(ServerUrl));
                    });
                    return;
                }
            }

            await _secretsManager.SaveServerUrlAsync(cleanUrl);
            _settings.Settings.ServerUrl = cleanUrl;

            var isAuth = await _accountService.IsAuthenticatedAsync();
            if (isAuth)
            {
                await _accountService.LogoutAsync();
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _previousServerUrl = cleanUrl;
                ServerUrl = cleanUrl;

                if (isAuth)
                {
                    _notificationService.ShowSuccess("Server URL updated. Please log in to use the new server instance.");
                }
                else
                {
                    _notificationService.ShowSuccess("Server URL updated successfully.");
                }
            });
        }
        finally
        {
            _isUpdatingServerUrl = false;
        }
    }
}