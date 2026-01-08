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
    
    // --- New Properties for Password Reset ---
    [ObservableProperty] private bool _isResetPasswordVisible;
    [ObservableProperty] private string _currentPassword = string.Empty;
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private string _resetPasswordError = string.Empty;

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
            // Handle offline/error state if needed
            App.NotificationService.ShowError("Device Offline");
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
            // Handle error
        }
    }

    // --- New Commands for Password Reset ---

    [RelayCommand]
    private void OpenResetPassword()
    {
        ResetInputs();
        IsResetPasswordVisible = true;
    }

    [RelayCommand]
    private void CancelResetPassword()
    {
        IsResetPasswordVisible = false;
        ResetInputs();
    }

    [RelayCommand]
    private async Task SubmitResetPasswordAsync()
    {
        if (IsBusy)
            return;

        ResetPasswordError = string.Empty;

        if (string.IsNullOrWhiteSpace(CurrentPassword) || 
            string.IsNullOrWhiteSpace(NewPassword) || 
            string.IsNullOrWhiteSpace(ConfirmPassword))
        {
            ResetPasswordError = "All fields are required.";
            return;
        }

        if (NewPassword.Length < 8)
        {
            ResetPasswordError = "Password must be at least 8 characters.";
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            ResetPasswordError = "New passwords do not match.";
            return;
        }

        IsBusy = true;
        try
        {
            await _accountService.ChangePasswordAsync(CurrentPassword, NewPassword);
            App.NotificationService.ShowSuccess("Password updated.");
            IsResetPasswordVisible = false;
            ResetInputs();
        }
        catch (InvalidCredentialsException)
        {
            ResetPasswordError = "Current password is incorrect.";
        }
        catch (InvalidRequestException ex)
        {
            ResetPasswordError = ex.Message;
        }
        catch (NetworkFailureException)
        {
            ResetPasswordError = "Network error. Try again.";
        }
        catch (ServerFailureException ex)
        {
            ResetPasswordError = ex.Message;
        }
        catch (Exception)
        {
            ResetPasswordError = "Failed to update password. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ResetInputs()
    {
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        ResetPasswordError = string.Empty;
    }
}