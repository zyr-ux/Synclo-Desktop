using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Synclo.Components;
using Synclo.Factory;

namespace Synclo.Services.Utilities;

public interface IDialogService
{
    Task<bool> ShowConfirmationAsync(
        string title,
        string message = "Are you sure you want to proceed with this action?",
        string confirmText = "Yes",
        string cancelText = "No");

    Task<bool?> ShowResetPasswordAsync();

    bool IsDialogOpen { get; }
    event EventHandler<bool>? IsDialogOpenChanged;
}

public class DialogService : IDialogService
{
    private readonly IViewModelFactory _factory;
    private int _activeDialogCount;

    public bool IsDialogOpen => _activeDialogCount > 0;
    public event EventHandler<bool>? IsDialogOpenChanged;

    public DialogService(IViewModelFactory factory)
    {
        _factory = factory;
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
        string cancelText)
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
            var dialog = new ConfirmationDialog(title, message, confirmText, cancelText);
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
            var dialog = new ResetPasswordDialogView();
            var viewModel = _factory.Create<ResetPasswordDialogViewModel>((Action<bool?>)(res => dialog.Close(res)));
            dialog.DataContext = viewModel;

            return await dialog.ShowDialog<bool?>(desktop.MainWindow);
        }
        finally
        {
            DecrementDialogCount();
        }
    }
}