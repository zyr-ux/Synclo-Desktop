using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Components;
using Synclo.Factory;
using Synclo.Models;
using Synclo.Services;
using Synclo.Services.API;
using Synclo.Services.Utilities;

namespace Synclo.ViewModels;

public partial class AccountDetailsViewModel : ViewModelBase
{
    private readonly IAccountService _accountService;
    private readonly IDeviceService _deviceService;
    private readonly IViewModelFactory _factory;
    private readonly INotificationService _notificationService;
    private readonly IDialogService _dialogService;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<DeviceModel> Devices { get; } = new();

    public event Action? LoggedOut;

    public AccountDetailsViewModel(
        IAccountService accountService,
        IDeviceService deviceService,
        IViewModelFactory factory,
        INotificationService notificationService,
        IDialogService dialogService)
    {
        _factory = factory;
        _accountService = accountService;
        _deviceService = deviceService;
        _notificationService = notificationService;
        _dialogService = dialogService;

        _ = InitializeAsync();
    }
    
    private async Task InitializeAsync()
    {
        var email = await _accountService.GetStoredEmailAsync() ?? "";
        var at = email.IndexOf('@');
        Username = at > 0 ? email[..at] : email;
        
        await LoadDevicesAsync();
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
            // Network or server error - show offline message
            _notificationService.ShowError("Device Offline");
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationAsync("Logout");
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
        var confirmed = await _dialogService.ShowConfirmationAsync("Delete Account");
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
}
