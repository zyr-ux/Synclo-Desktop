using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Components;
using Synclo.Models;
using Synclo.Services;

namespace Synclo.ViewModels;

public partial class AccountDetailsViewModel : ViewModelBase
{
    private readonly AccountService _accountService;
    private readonly DeviceService _deviceService;
    private readonly Action _onLogout;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<DeviceModel> Devices { get; } = new();

    public AccountDetailsViewModel(
        AccountService accountService,
        DeviceService deviceService,
        string email,
        Action onLogout)
    {
        _accountService = accountService;
        _deviceService = deviceService;
        _onLogout = onLogout;

        var at = email.IndexOf('@');
        Username = at > 0 ? email[..at] : email;

        _ = LoadDevicesAsync();
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
        catch
        {
            App.NotificationService.ShowError("Device Offline");
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        IsBusy = true;
        try
        {
            await _accountService.LogoutAsync();
            Devices.Clear();
            _onLogout();
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
        catch
        {
        }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        var dialog = new ResetPasswordDialogView();
        var viewModel = new ResetPasswordDialogViewModel(res => dialog.Close(res));
        dialog.DataContext = viewModel;
        
        var desktop =
            Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

        viewModel.SetAccountService(_accountService);
        var result = await dialog.ShowDialog<bool?>(desktop?.MainWindow!);
        if (result == true)
            App.NotificationService.ShowSuccess("Password updated.");
    }
    
    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        var confirmed = await App.DialogService.ShowConfirmationAsync("Delete Account");
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            await _accountService.DeleteAccountAsync();
            Devices.Clear();
            _onLogout();
        }
        catch (NetworkFailureException)
        {
            App.NotificationService.ShowError("Network error.");
        }
        catch
        {
            App.NotificationService.ShowError("Failed to delete account.");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
