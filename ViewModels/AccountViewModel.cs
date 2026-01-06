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

public partial class AccountViewModel : ViewModelBase
{
    private readonly AccountService _accountService;
    private readonly DeviceService _deviceService;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    public ObservableCollection<DeviceModel> Devices { get; } = new();

    public AccountViewModel()
    {
        _accountService = App.APIService.AccountService;
        _deviceService = App.APIService.DeviceService;
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
    private async Task LoginAsync()
    {
        if (IsBusy) return;
        
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        
        if (string.IsNullOrWhiteSpace(Email))
        {
            App.NotificationService.ShowWarning("Email is required", "Login");
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            App.NotificationService.ShowWarning("Password is required", "Login");
            return;
        }

        IsBusy = true;
        StatusMessage = "Logging in...";

        try
        {
            await _accountService.LoginAsync(Email, Password);
            Password = string.Empty;
            StatusMessage = string.Empty;
            App.NotificationService.ShowSuccess("Logged in successfully.", "Login");
            await OnAuthSuccess();
        }
        catch (InvalidRequestException ex)
        {
            App.NotificationService.ShowWarning(ex.Message, "Login");
        }
        catch (InvalidCredentialsException)
        {
            App.NotificationService.ShowError("Incorrect email or password.", "Login");
        }
        catch (SecurityBreachException)
        {
            App.NotificationService.ShowError("Your session was terminated for security. Please log in again.", "Login");
        }
        catch (NetworkFailureException)
        {
            App.NotificationService.ShowError("Network issue detected. Please check your connection.", "Login");
        }
        catch (ServerFailureException)
        {
            App.NotificationService.ShowError("Something went wrong on our side. Please try again.", "Login");
        }
        catch (Exception)
        {
            App.NotificationService.ShowError("Login failed. Please try again.", "Login");
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
            ErrorMessage = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (IsBusy) return;
        
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        
        if (string.IsNullOrWhiteSpace(Email))
        {
            App.NotificationService.ShowWarning("Email is required", "Register");
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            App.NotificationService.ShowWarning("Password is required", "Register");
            return;
        }

        if (Password.Length < 8)
        {
            App.NotificationService.ShowWarning("Password must be at least 8 characters", "Register");
            return;
        }

        IsBusy = true;
        StatusMessage = "Creating account...";

        try
        {
            await _accountService.RegisterAsync(Email, Password);
            Password = string.Empty;
            App.NotificationService.ShowSuccess("Account created successfully.", "Register");
            await OnAuthSuccess();
        }
        catch (InvalidRequestException ex)
        {
            App.NotificationService.ShowWarning(ex.Message, "Register");
        }
        catch (UserAlreadyExistsException)
        {
            App.NotificationService.ShowError("This email is already registered.", "Register");
        }
        catch (NetworkFailureException)
        {
            App.NotificationService.ShowError("Network issue detected. Please check your connection.", "Register");
        }
        catch (ServerFailureException)
        {
            App.NotificationService.ShowError("Something went wrong on our side. Please try again.", "Register");
        }
        catch (Exception)
        {
            App.NotificationService.ShowError("Registration failed. Please try again.", "Register");
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
            ErrorMessage = string.Empty;
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
                await _deviceService.SaveAsync(Devices.ToList());
            }
        }
        catch
        {
        }
    }
}