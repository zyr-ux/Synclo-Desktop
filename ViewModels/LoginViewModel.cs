using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Models;
using Synclo.Services;

namespace Synclo.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly AccountService _accountService;
    private readonly Action<string> _onLoginSuccess;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public LoginViewModel(AccountService accountService, Action<string> onLoginSuccess)
    {
        _accountService = accountService;
        _onLoginSuccess = onLoginSuccess;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;

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
            _onLoginSuccess(Email);
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
        }
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (IsBusy) return;

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
            _onLoginSuccess(Email);
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
        }
    }
}

