using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Synclo.Features.Network_Services;

namespace Synclo.Features.Dialog_Manager.Reset_Password_Dialog;

public partial class ResetPasswordDialogView : Window
{
    private readonly IAccountService _accountService;

    // For Avalonia previewer / designer
    public ResetPasswordDialogView()
    {
        InitializeComponent();
        _accountService = null!;
    }

    public ResetPasswordDialogView(IAccountService accountService) : this()
    {
        _accountService = accountService;
    }
    
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close(false);

        base.OnKeyDown(e);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async void OnResetClick(object? sender, RoutedEventArgs e)
    {
        var currentPassword = CurrentPasswordBox.Text;
        var newPassword = NewPasswordBox.Text;
        var confirmPassword = ConfirmPasswordBox.Text;

        ErrorTextBlock.IsVisible = false;
        StatusTextBlock.IsVisible = false;

        if (string.IsNullOrWhiteSpace(currentPassword) ||
            string.IsNullOrWhiteSpace(newPassword) ||
            string.IsNullOrWhiteSpace(confirmPassword))
        {
            ErrorTextBlock.Text = "All fields are required.";
            ErrorTextBlock.IsVisible = true;
            return;
        }

        if (newPassword.Length < 8)
        {
            ErrorTextBlock.Text = "Password must be at least 8 characters.";
            ErrorTextBlock.IsVisible = true;
            return;
        }

        if (newPassword != confirmPassword)
        {
            ErrorTextBlock.Text = "Passwords do not match.";
            ErrorTextBlock.IsVisible = true;
            return;
        }

        SetBusy(true);
        StatusTextBlock.Text = "Updating password...";
        StatusTextBlock.IsVisible = true;

        try
        {
            await _accountService.ChangePasswordAsync(currentPassword, newPassword);
            Close(true);
        }
        catch (Synclo.Models.InvalidCredentialsException)
        {
            StatusTextBlock.IsVisible = false;
            ErrorTextBlock.Text = "Current password is incorrect.";
            ErrorTextBlock.IsVisible = true;
        }
        catch (Synclo.Models.InvalidRequestException ex)
        {
            StatusTextBlock.IsVisible = false;
            ErrorTextBlock.Text = ex.Message;
            ErrorTextBlock.IsVisible = true;
        }
        catch (Synclo.Models.NetworkFailureException)
        {
            StatusTextBlock.IsVisible = false;
            ErrorTextBlock.Text = "Network error.";
            ErrorTextBlock.IsVisible = true;
        }
        catch (Synclo.Models.ServerFailureException ex)
        {
            StatusTextBlock.IsVisible = false;
            ErrorTextBlock.Text = ex.Message;
            ErrorTextBlock.IsVisible = true;
        }
        catch (Exception)
        {
            StatusTextBlock.IsVisible = false;
            ErrorTextBlock.Text = "Failed to update password.";
            ErrorTextBlock.IsVisible = true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        CurrentPasswordBox.IsEnabled = !isBusy;
        NewPasswordBox.IsEnabled = !isBusy;
        ConfirmPasswordBox.IsEnabled = !isBusy;
        CancelButton.IsEnabled = !isBusy;
        ResetButton.IsEnabled = !isBusy;
    }
}