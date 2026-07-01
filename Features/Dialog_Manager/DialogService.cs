using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Synclo.Features.Dialog_Manager.Confirmation_Dialog;
using Synclo.Features.Dialog_Manager.Reset_Password_Dialog;
using Synclo.Features.Network_Services;
using Synclo.Utilities;

namespace Synclo.Features.Dialog_Manager;

public interface IDialogService
{
    Task<bool> ShowConfirmationAsync(
        string title,
        string message = "Are you sure you want to proceed with this action?",
        string confirmText = "Yes",
        string cancelText = "No",
        bool isDangerous = false);

    Task<bool?> ShowResetPasswordAsync();

    bool IsDialogOpen { get; }
    event EventHandler<bool>? IsDialogOpenChanged;
}

public class DialogService : IDialogService
{
    private readonly IViewModelFactory _factory;
    private readonly IAccountService _accountService;
    private int _activeDialogCount;

    public bool IsDialogOpen => _activeDialogCount > 0;
    public event EventHandler<bool>? IsDialogOpenChanged;

    public DialogService(IViewModelFactory factory, IAccountService accountService)
    {
        _factory = factory;
        _accountService = accountService;
    }

    private void IncrementDialogCount()
    {
        var wasOpen = IsDialogOpen;
        _activeDialogCount++;
        if (IsDialogOpen != wasOpen)
        {
            IsDialogOpenChanged?.Invoke(this, true);
        }
    }

    private void DecrementDialogCount()
    {
        var wasOpen = IsDialogOpen;
        _activeDialogCount = Math.Max(0, _activeDialogCount - 1);
        if (IsDialogOpen != wasOpen)
        {
            IsDialogOpenChanged?.Invoke(this, false);
        }
    }

    public async Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool isDangerous = false)
    {
        if (Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is null)
        {
            throw new InvalidOperationException("MainWindow is not available.");
        }

        IncrementDialogCount();
        try
        {
            var dialog = new ConfirmationDialog(title, message, confirmText, cancelText, isDangerous);
            return await dialog.ShowDialog<bool>(desktop.MainWindow);
        }
        finally
        {
            DecrementDialogCount();
        }
    }

    public async Task<bool?> ShowResetPasswordAsync()
    {
        if (Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is null)
        {
            throw new InvalidOperationException("MainWindow is not available.");
        }

        IncrementDialogCount();
        try
        {
            var dialog = new ResetPasswordDialogView(_accountService);
            return await dialog.ShowDialog<bool?>(desktop.MainWindow);
        }
        finally
        {
            DecrementDialogCount();
        }
    }
}