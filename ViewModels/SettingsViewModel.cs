using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Synclo.Models;
using Synclo.Features.Settings_Manager;
using Synclo.Features.Network_Services;
using Synclo.Features.Clipboard_Manager.Clipboard_Service;
using Synclo.Features.Startup_Manager;
using Synclo.Utilities;
using Synclo.Features.Secrets_Manager;
using Synclo.Features.Notifications_Manager;
using Synclo.Features.Dialog_Manager;
using Synclo.Features.Font_Manager;
using Synclo.Themes;

namespace Synclo.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IApiService _apiService;
    private readonly IThemeService _themeService;
    private readonly IFontManager _fontManager;
    private readonly IStartupManager _startupManager;
    private readonly IAccountService _accountService;
    private readonly INotificationService _notificationService;
    private readonly ISecretsManager _secretsManager;
    private readonly IUtils _utils;

    [ObservableProperty] private string _selectedTheme = "System";
    [ObservableProperty] private string _selectedFont = "Inconsolata";
    [ObservableProperty] private bool _showResult;
    [ObservableProperty] private bool _isStartOnBootEnabled;
    [ObservableProperty] private bool _isMicaEnabled;
    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private string _serverUrlInput = "";
    [ObservableProperty] private string _selectedCloseBehavior="";
    [ObservableProperty] private string _selectedRightClickAction = "Context Menu";
    
    private string _previousServerUrl = "";
    private bool _isUpdatingServerUrl;
    private bool CanExecuteServerUrlCommand() => !_isUpdatingServerUrl;
    public bool IsMicaToggleVisible => OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 22000;
    public List<string> AvailableThemes { get; } = ["System", "Light", "Dark"];
    public List<string> AvailableFonts { get; } = [..Enum.GetNames<AppFonts>()];
    public List<string> AvailableCloseBehaviors { get; } = ["Quit Application", "Minimize to System Tray", "Run in Background (Hidden)"];
    public List<string> AvailableRightClickActions { get; } = ["Context Menu", "Pin / Unpin", "Delete"];

    public SettingsViewModel(
        ISettingsService settings, 
        IApiService apiService, 
        IThemeService themeService,
        IFontManager fontManager,
        IStartupManager startupManager,
        IAccountService accountService,
        INotificationService notificationService,
        ISecretsManager secretsManager,
        IUtils utils)
    {
        _settings = settings;
        _apiService = apiService;
        _themeService = themeService;
        _fontManager = fontManager;
        _startupManager = startupManager;
        _accountService = accountService;
        _notificationService = notificationService;
        _secretsManager = secretsManager;
        _utils = utils;
        
        SelectedTheme = _settings.Settings.Theme;
        SelectedFont = string.IsNullOrWhiteSpace(_settings.Settings.FontFamily) ? "Inconsolata" : _settings.Settings.FontFamily;
        IsMicaEnabled = _settings.Settings.is_mica_enabled;
        ServerUrl = _settings.Settings.ServerUrl;
        ServerUrlInput = ServerUrl;
        _previousServerUrl = ServerUrl;
        
        // Load start on boot status
        _ = LoadStartOnBootStatusAsync();
        
        // Load other settings
        InitializeCloseBehavior();
        InitializeRightClickAction();
    }

    partial void OnSelectedFontChanged(string value)
    {
        _fontManager.ApplyFont(value);
        _settings.Settings.FontFamily = value;
        _settings.Save();
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

    private void InitializeRightClickAction()
    {
        var action = _settings.Settings.RightClickAction;
        SelectedRightClickAction = action switch
        {
            "Pin" => "Pin / Unpin",
            "Delete" => "Delete",
            _ => "Context Menu"
        };
    }

    partial void OnSelectedRightClickActionChanged(string value)
    {
        switch (value)
        {
            case "Context Menu":
                _settings.Settings.RightClickAction = "ContextMenu";
                break;
            case "Pin / Unpin":
                _settings.Settings.RightClickAction = "Pin";
                break;
            case "Delete":
                _settings.Settings.RightClickAction = "Delete";
                break;
        }

        _settings.Save();
    }

    [RelayCommand(CanExecute = nameof(CanExecuteServerUrlCommand))]
    public async Task UpdateServerUrlAsync()
    {
        if (_isUpdatingServerUrl) 
            return;
        
        _isUpdatingServerUrl = true;
        UpdateServerUrlCommand.NotifyCanExecuteChanged();
        ResetServerUrlCommand.NotifyCanExecuteChanged();

        try
        {
            var text = ServerUrlInput;
            if (!_utils.TryNormalizeServerUrl(text, out var cleanUrl, out var error))
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _notificationService.ShowError(error!);
                });
                return;
            }

            if (cleanUrl == _previousServerUrl)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (cleanUrl == AppSettings.DefaultServerUrl)
                    {
                        _notificationService.ShowSuccess("Already using the default server instance.");
                    }
                    else
                    {
                        _notificationService.ShowSuccess("Already using this server instance.");
                    }
                });
                return;
            }

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
                        ServerUrlInput = ServerUrl;
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
                ServerUrlInput = cleanUrl;

                if (cleanUrl == AppSettings.DefaultServerUrl)
                {
                    if (isAuth)
                    {
                        _notificationService.ShowSuccess("Server URL reset to default. Please log in to continue.");
                    }
                    else
                    {
                        _notificationService.ShowSuccess("Server URL reset to default.");
                    }
                }
                else
                {
                    if (isAuth)
                    {
                        _notificationService.ShowSuccess("Server URL updated. Please log in to use the new server instance.");
                    }
                    else
                    {
                        _notificationService.ShowSuccess("Server URL updated successfully.");
                    }
                }
            });
        }
        finally
        {
            _isUpdatingServerUrl = false;
            UpdateServerUrlCommand.NotifyCanExecuteChanged();
            ResetServerUrlCommand.NotifyCanExecuteChanged();
        }
     }

    [RelayCommand(CanExecute = nameof(CanExecuteServerUrlCommand))]
    public async Task ResetServerUrlAsync()
    {
        if (_isUpdatingServerUrl)
            return;
        
        ServerUrlInput = AppSettings.DefaultServerUrl;
        await UpdateServerUrlAsync();
    }
}