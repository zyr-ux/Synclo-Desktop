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
    private readonly NotificationService _notificationService;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public event Action? LoginSucceeded;

    public LoginViewModel(AccountService accountService, NotificationService notificationService)
    {
        _accountService = accountService;
        _notificationService = notificationService;
    }

    [RelayCommand]
    private async Task LoginAsync() 
    {
        if (IsBusy) return;

        StatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Email))
        {
            _notificationService.ShowWarning("Email is required", "Login");
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            _notificationService.ShowWarning("Password is required", "Login");
            return;
        }

        IsBusy = true;
        StatusMessage = "Logging in...";

        try
        {
            await _accountService.LoginAsync(Email, Password);
            Password = string.Empty;
            StatusMessage = string.Empty;
            _notificationService.ShowSuccess("Logged in successfully.", "Login");
            LoginSucceeded?.Invoke();
        }
        catch (InvalidRequestException ex)
        {
            _notificationService.ShowWarning(ex.Message, "Login");
        }
        catch (InvalidCredentialsException)
        {
            _notificationService.ShowError("Incorrect email or password.", "Login");
        }
        catch (SecurityBreachException)
        {
            _notificationService.ShowError("Your session was terminated for security. Please log in again.", "Login");
        }
        catch (NetworkFailureException)
        {
            _notificationService.ShowError("Network issue detected. Please check your connection.", "Login");
        }
        catch (ServerFailureException)
        {
            _notificationService.ShowError("Something went wrong on our side. Please try again.", "Login");
        }
        catch (Exception)
        {
            _notificationService.ShowError("Login failed. Please try again.", "Login");
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
            _notificationService.ShowWarning("Email is required", "Register");
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            _notificationService.ShowWarning("Password is required", "Register");
            return;
        }

        if (Password.Length < 8)
        {
            _notificationService.ShowWarning("Password must be at least 8 characters", "Register");
            return;
        }

        IsBusy = true;
        StatusMessage = "Creating account...";

        try
        {
            await _accountService.RegisterAsync(Email, Password);
            Password = string.Empty;
            _notificationService.ShowSuccess("Account created successfully.", "Register");
            LoginSucceeded?.Invoke();
        }
        catch (InvalidRequestException ex)
        {
            _notificationService.ShowWarning(ex.Message, "Register");
        }
        catch (UserAlreadyExistsException)
        {
            _notificationService.ShowError("This email is already registered.", "Register");
        }
        catch (NetworkFailureException)
        {
            _notificationService.ShowError("Network issue detected. Please check your connection.", "Register");
        }
        catch (ServerFailureException)
        {
            _notificationService.ShowError("Something went wrong on our side. Please try again.", "Register");
        }
        catch (Exception)
        {
            _notificationService.ShowError("Registration failed. Please try again.", "Register");
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
        }
    }
}

