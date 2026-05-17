using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Synclo.Components;

namespace Synclo.Services.Utilities;

public interface IDialogService
{
    Task<bool> ShowConfirmationAsync(
        string title,
        string message = "Are you sure you want to proceed with this action?",
        string confirmText = "Yes",
        string cancelText = "No");
}

public class DialogService : IDialogService
{
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