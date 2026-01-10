using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Models;
using Synclo.Services;
using Synclo.ViewModels;

namespace Synclo.Components;

public partial class ResetPasswordDialogViewModel(
    System.Action<bool?> close)
    : ViewModelBase
{
    private AccountService _accountService;

    [ObservableProperty] private string _currentPassword = string.Empty;
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public void SetAccountService(AccountService accountService)
    {
        _accountService = accountService;
    }

    [RelayCommand]
    private void Cancel()
    {
        close(false);
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsBusy)
            return;

        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(CurrentPassword) ||
            string.IsNullOrWhiteSpace(NewPassword) ||
            string.IsNullOrWhiteSpace(ConfirmPassword))
        {
            ErrorMessage = "All fields are required.";
            return;
        }

        if (NewPassword.Length < 8)
        {
            ErrorMessage = "Password must be at least 8 characters.";
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Updating password...";
        try
        {
            await _accountService.ChangePasswordAsync(CurrentPassword, NewPassword);
            close(true);
        }
        catch (InvalidCredentialsException)
        {
            ErrorMessage = "Current password is incorrect.";
        }
        catch (InvalidRequestException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (NetworkFailureException)
        {
            ErrorMessage = "Network error.";
        }
        catch (ServerFailureException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch
        {
            ErrorMessage = "Failed to update password.";
        }
        finally
        {
            IsBusy = false;
            StatusMessage = string.Empty;
        }
    }
}
