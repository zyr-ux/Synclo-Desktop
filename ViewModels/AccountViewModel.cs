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

public partial class AccountViewModel : ViewModelBase
{
    private readonly AccountService _accountService;
    private readonly DeviceCacheService _deviceCacheService;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    public ObservableCollection<DeviceModel> Devices { get; } = new();

    public AccountViewModel()
    {
        _accountService = App.APIService.AccountService;
        _deviceCacheService = App.APIService.DeviceCacheService;
        _ = InitializeAsync();
    }
    
    private async Task InitializeAsync()
    {
        if (await _accountService.IsAuthenticatedAsync())
        {
            IsLoggedIn = true;
            Email = await _accountService.GetStoredEmailAsync() ?? "";
            var atIndex = Email.IndexOf('@');
            Username = atIndex > 0 ? Email.Substring(0, atIndex) : Email;

            await LoadDevicesAsync();
        }
    }

    private async Task OnAuthSuccess()
    {
        var atIndex = Email.IndexOf('@');
        Username = atIndex > 0 ? Email.Substring(0, atIndex) : Email;
        IsLoggedIn = true;

        await LoadDevicesAsync();
        _ = App.APIService.WebSocketService.ConnectAsync();
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
        // 1. Show cached data immediately
        var cached = await _deviceCacheService.LoadAsync();
        UpdateDeviceList(cached);

        // 2. Fetch fresh data from API
        try
        {
            var fresh = await App.APIService.DeviceService.GetDevicesAsync();
            UpdateDeviceList(fresh);
            await _deviceCacheService.SaveAsync(fresh);
        }
        catch (SessionExpiredException)
        {
            await LogoutAsync();
        }
        catch
        {
            /* Fallback to cached list is already shown */
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            await _accountService.LoginAsync(Email, Password);
            Password = string.Empty;
            await OnAuthSuccess();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            await _accountService.RegisterAsync(Email, Password);
            Password = string.Empty;
            await OnAuthSuccess();
        }
        finally
        {
            IsBusy = false;
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
            Email = Password = Username = string.Empty;
            IsLoggedIn = false;
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
                await _deviceCacheService.SaveAsync(Devices.ToList());
            }
        }
        catch
        {
            /* Handle error */
        }
    }
}