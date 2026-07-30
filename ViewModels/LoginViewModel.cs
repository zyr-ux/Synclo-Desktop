using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Models;
using Synclo.Features.Connection_Monitor;
using Synclo.Features.Network_Services;
using Synclo.Features.Notifications_Manager;
using Synclo.Features.Settings_Manager;
using Synclo.Utilities;

namespace Synclo.ViewModels;

public partial class LoginViewModel : ViewModelBase, IDisposable
{
    private readonly IAccountService _accountService;
    private readonly INotificationService _notificationService;
    private readonly IUtils _utils;
    private readonly ISettingsService _settingsService;
    private readonly IConnectionMonitor _connectionMonitor;

    [ObservableProperty]
    private ConnectionStatus _connectionStatus;

    public string ServerUrl => _settingsService.Settings.ServerUrl;
    public string ServerType => ServerUrl == AppSettings.DefaultServerUrl ? "Public Server" : "Self-hosted Server";

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmitButtonText))]
    private bool _isRegisterMode;

    public string SubmitButtonText => IsRegisterMode ? "Register" : "Login";

    public event Action? LoginSucceeded;

    public LoginViewModel(
        IAccountService accountService, 
        INotificationService notificationService,
        IUtils utils,
        ISettingsService settingsService,
        IConnectionMonitor connectionMonitor)
    {
        _accountService = accountService;
        _notificationService = notificationService;
        _utils = utils;
        _settingsService = settingsService;
        _connectionMonitor = connectionMonitor;

        ConnectionStatus = _connectionMonitor.ConnectionStatus;
        _connectionMonitor.ConnectionStatusChanged += HandleConnectionStatusChanged;
        _settingsService.SettingsChanged += HandleSettingsChanged;
    }

    private void HandleConnectionStatusChanged(ConnectionStatus status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = status;
        });
    }

    private void HandleSettingsChanged(AppSettings settings)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ServerUrl));
            OnPropertyChanged(nameof(ServerType));
        });
    }

    partial void OnPasswordChanged(string value) => ValidatePasswordsRealTime();
    partial void OnConfirmPasswordChanged(string value) => ValidatePasswordsRealTime();

    partial void OnIsRegisterModeChanged(bool value)
    {
        ConfirmPassword = string.Empty;
        StatusMessage = string.Empty;
    }

    private void ValidatePasswordsRealTime()
    {
        if (!IsRegisterMode || IsBusy) return;

        if (_utils.ValidatePassword(Password, ConfirmPassword, out var error))
        {
            if (StatusMessage == "Passwords do not match" || StatusMessage == "Password must be at least 8 characters")
            {
                StatusMessage = string.Empty;
            }
        }
        else
        {
            StatusMessage = error ?? string.Empty;
        }
    }

    [RelayCommand]
    private void SwitchMode()
    {
        IsRegisterMode = !IsRegisterMode;
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsRegisterMode)
        {
            await RegisterAsync();
        }
        else
        {
            await LoginAsync();
        }
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

        if (!IsValidEmail(Email))
        {
            _notificationService.ShowWarning("Please enter a valid email address", "Login");
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

        if (!IsValidEmail(Email))
        {
            _notificationService.ShowWarning("Please enter a valid email address", "Register");
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            _notificationService.ShowWarning("Password is required", "Register");
            return;
        }

        if (Password.Length < 8)
        {
            StatusMessage = "Password must be at least 8 characters";
            _notificationService.ShowWarning("Password must be at least 8 characters", "Register");
            return;
        }

        if (Password != ConfirmPassword)
        {
            StatusMessage = "Passwords do not match";
            _notificationService.ShowWarning("Passwords do not match", "Register");
            return;
        }

        IsBusy = true;
        StatusMessage = "Creating account...";

        try
        {
            await _accountService.RegisterAsync(Email, Password, string.IsNullOrWhiteSpace(Username) ? null : Username.Trim());
            Password = string.Empty;
            ConfirmPassword = string.Empty;
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

    private static bool IsValidEmail(string email)
    {
        // Simple email validation regex
        var pattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
        return Regex.IsMatch(email, pattern, RegexOptions.IgnoreCase);
    }

    public void Dispose()
    {
        _connectionMonitor.ConnectionStatusChanged -= HandleConnectionStatusChanged;
        _settingsService.SettingsChanged -= HandleSettingsChanged;
    }
}
