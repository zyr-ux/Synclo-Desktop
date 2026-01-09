using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Synclo.Views;

namespace Synclo.Services;

public class DialogService : DialogService.IDialogService
{
    public interface IDialogService
    {
        Task<bool> ShowConfirmationAsync(
            string title,
            string message = "Are you sure you want to proceed with this action?",
            string confirmText = "Yes",
            string cancelText = "No");
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

        var dialog = new ConfirmationDialog(title, message, confirmText, cancelText);

        return await dialog.ShowDialog<bool>(desktop.MainWindow);
    }
}