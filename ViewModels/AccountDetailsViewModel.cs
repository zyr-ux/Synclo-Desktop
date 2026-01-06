using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public AccountDetailsViewModel(AccountService accountService, DeviceService deviceService, string email, Action onLogout)
    {
        _accountService = accountService;
        _deviceService = deviceService;
        _onLogout = onLogout;
        
        var atIndex = email.IndexOf('@');
        Username = atIndex > 0 ? email.Substring(0, atIndex) : email;
        
        _ = LoadDevicesAsync();
    }

    private void UpdateDeviceList(IEnumerable<DeviceModel> list)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Devices.Clear();
            foreach (var d in list) Devices.Add(d);
        });
    }

    private async Task LoadDevicesAsync()
    {
        var cached = await _deviceService.LoadAsync();
        UpdateDeviceList(cached);

        try
        {
            var fresh = await App.APIService.DeviceService.GetDevicesAsync();
            UpdateDeviceList(fresh);
            await _deviceService.SaveAsync(fresh);
        }
        catch (SessionExpiredException)
        {
            await LogoutAsync();
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDevicesAsync();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        IsBusy = true;
        try
        {
            await _accountService.LogoutAsync();
            await App.APIService.WebSocketService.DisconnectAsync();
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
            await App.APIService.DeviceService.DeleteDeviceAsync(deviceId);
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
}

