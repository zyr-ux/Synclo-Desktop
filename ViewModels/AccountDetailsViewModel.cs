using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Utilities;
using Synclo.Models;
using Synclo.Features.Network_Services;
using Synclo.Features.Notifications_Manager;
using Synclo.Features.Dialog_Manager;
using Synclo.Features.Clipboard_Manager.Clipboard_Monitor;

namespace Synclo.ViewModels;

public partial class AccountDetailsViewModel : ViewModelBase, IDisposable
{
    private readonly IAccountService _accountService;
    private readonly IDeviceService _deviceService;
    private readonly IViewModelFactory _factory;
    private readonly INotificationService _notificationService;
    private readonly IDialogService _dialogService;
    private readonly IWebSocketService _webSocketService;
    private readonly IClipboardMonitor _clipboardMonitor;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _userId = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<DeviceModel> Devices { get; } = new();

    public event Action? LoggedOut;

    public AccountDetailsViewModel(
        IAccountService accountService,
        IDeviceService deviceService,
        IViewModelFactory factory,
        INotificationService notificationService,
        IDialogService dialogService,
        IWebSocketService webSocketService,
        IClipboardMonitor clipboardMonitor)
    {
        _factory = factory;
        _accountService = accountService;
        _deviceService = deviceService;
        _notificationService = notificationService;
        _dialogService = dialogService;
        _webSocketService = webSocketService;
        _clipboardMonitor = clipboardMonitor;

        _webSocketService.OnDeviceAdded += OnDeviceAddedHandler;
        _webSocketService.OnDeviceUpdated += OnDeviceUpdatedHandler;
        _webSocketService.OnDeviceDeleted += OnDeviceDeletedHandler;
        _webSocketService.OnUsernameUpdated += OnUsernameUpdatedHandler;

        _ = InitializeAsync();
    }
    
    private async Task InitializeAsync()
    {
        var storedEmail = await _accountService.GetStoredEmailAsync();
        if (!string.IsNullOrWhiteSpace(storedEmail))
        {
            Email = storedEmail;
        }

        var storedUsername = await _accountService.GetStoredUsernameAsync();
        if (!string.IsNullOrWhiteSpace(storedUsername))
        {
            Username = storedUsername;
        }
        else if (!string.IsNullOrWhiteSpace(storedEmail))
        {
            var at = storedEmail.IndexOf('@');
            Username = at > 0 ? storedEmail[..at] : storedEmail;
        }

        var storedUserId = await _accountService.GetStoredUserIdAsync();
        if (!string.IsNullOrWhiteSpace(storedUserId))
        {
            UserId = storedUserId;
        }

        _ = RefreshUserProfileInBackgroundAsync();

        await LoadDevicesAsync();
    }

    private async Task RefreshUserProfileInBackgroundAsync()
    {
        try
        {
            var profile = await _accountService.GetUserProfileAsync();
            if (profile != null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!string.IsNullOrWhiteSpace(profile.username))
                        Username = profile.username;
                    if (!string.IsNullOrWhiteSpace(profile.email))
                        Email = profile.email;
                    if (!string.IsNullOrWhiteSpace(profile.user_id))
                        UserId = profile.user_id;
                });
            }
        }
        catch
        {
            // Network failure - fallback to stored credentials
        }
    }

    private void UpdateDevices(IEnumerable<DeviceModel> list)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Devices.Clear();
            foreach (var d in list)
                Devices.Add(d);
        });
    }

    private async Task LoadDevicesAsync()
    {
        var cached = await _deviceService.LoadAsync();
        UpdateDevices(cached);

        try
        {
            var fresh = await _deviceService.GetDevicesAsync();
            UpdateDevices(fresh);
            await _deviceService.SaveAsync(fresh);
        }
        catch (SessionExpiredException)
        {
            await LogoutAsync();
        }
        catch (DeviceNotFoundException)
        {
            // Device was deleted remotely - trigger logout
            // Note: AccountService.OnDeviceDeletedHandler already shows the notification via WebSocket
            await LogoutAsync();
        }
        catch
        {
            // Network or server error - fallback silently to cached list
        }
    }

    [RelayCommand]
    private void EditAccount()
    {
        // TODO: Implement edit account information logic
    }

    [RelayCommand]
    private async Task CopyUserIdAsync()
    {
        if (string.IsNullOrWhiteSpace(UserId))
            return;

        await _clipboardMonitor.SetClipboardTextAsync(UserId);
        _notificationService.ShowSuccess("Account ID copied to clipboard.");
    }

    [RelayCommand]
    private async Task CopyEmailAsync()
    {
        if (string.IsNullOrWhiteSpace(Email))
            return;

        await _clipboardMonitor.SetClipboardTextAsync(Email);
        _notificationService.ShowSuccess("Email copied to clipboard.");
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationAsync(
            title: "Logout",
            message: "Are you sure you want to log out of Synclo on this device?",
            confirmText: "Logout",
            cancelText: "Cancel",
            isDangerous: true);
        if (!confirmed)
            return;
        
        IsBusy = true;
        try
        {
            await _accountService.LogoutAsync();
            Devices.Clear();
            LoggedOut?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LogoutDeviceAsync(string deviceId)
    {
        try
        {
            await _deviceService.DeleteDeviceAsync(deviceId);
            var target = Devices.FirstOrDefault(x => x.device_id == deviceId);
            if (target != null)
            {
                Devices.Remove(target);
                await _deviceService.SaveAsync(Devices.ToList());
            }
        }
        catch (Exception)
        {
            _notificationService.ShowError("Failed to log out device. Please try again.");
        }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        var result = await _dialogService.ShowResetPasswordAsync();
        if (result == true)
            _notificationService.ShowSuccess("Password updated.");
    }
    
    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationAsync(
            title: "Delete Account",
            message: "Are you sure you want to permanently delete your account?",
            confirmText: "Delete",
            cancelText: "Cancel",
            isDangerous: true);
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            await _accountService.DeleteAccountAsync();
            Devices.Clear();
            LoggedOut?.Invoke();
        }
        catch (NetworkFailureException)
        {
            _notificationService.ShowError("Network error.");
        }
        catch
        {
            _notificationService.ShowError("Failed to delete account.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnDeviceAddedHandler(string deviceJson)
    {
        _ = LoadDevicesAsync();
    }

    private void OnDeviceUpdatedHandler(string deviceJson)
    {
        _ = LoadDevicesAsync();
    }

    private void OnDeviceDeletedHandler(string? deviceId)
    {
        if (deviceId != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var target = Devices.FirstOrDefault(x => x.device_id == deviceId);
                if (target != null)
                {
                    Devices.Remove(target);
                    _ = _deviceService.SaveAsync(Devices.ToList());
                }
            });
        }
    }

    private void OnUsernameUpdatedHandler(string newUsername)
    {
        if (!string.IsNullOrWhiteSpace(newUsername))
        {
            Dispatcher.UIThread.Post(() =>
            {
                Username = newUsername;
            });
        }
    }

    public void Dispose()
    {
        _webSocketService.OnDeviceAdded -= OnDeviceAddedHandler;
        _webSocketService.OnDeviceUpdated -= OnDeviceUpdatedHandler;
        _webSocketService.OnDeviceDeleted -= OnDeviceDeletedHandler;
        _webSocketService.OnUsernameUpdated -= OnUsernameUpdatedHandler;
    }
}
