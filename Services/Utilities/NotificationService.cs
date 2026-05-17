using System;
using Avalonia.Controls.Notifications;

namespace Synclo.Services.Utilities;

public interface INotificationService
{
    void SetManager(INotificationManager manager);
    void ShowError(string message, string? title = null);
    void ShowWarning(string message, string? title = null);
    void ShowSuccess(string message, string? title = null);
    void ShowInfo(string message, string? title = null);
}

public sealed class NotificationService : INotificationService
{
    private INotificationManager? _manager;

    public void SetManager(INotificationManager manager)
    {
        _manager = manager;
    }

    private void Show(string message, string? title, NotificationType type)
    {
        if (_manager is null)
            return;

        var timeout = type switch
        {
            NotificationType.Success => TimeSpan.FromSeconds(2),
            NotificationType.Information => TimeSpan.FromSeconds(3),
            NotificationType.Warning => TimeSpan.FromSeconds(5),
            NotificationType.Error => TimeSpan.FromSeconds(0),
            _ => TimeSpan.FromSeconds(3)
        };

        _manager.Show(new Notification(title ?? type.ToString(), message, type, timeout));
    }

    // Backward-compatible convenience wrappers
    public void ShowError(string message, string? title = null) =>
        Show(message, title, NotificationType.Error);

    public void ShowWarning(string message, string? title = null) =>
        Show(message, title, NotificationType.Warning);

    public void ShowSuccess(string message, string? title = null) =>
        Show(message, title, NotificationType.Success);

    public void ShowInfo(string message, string? title = null) =>
        Show(message, title, NotificationType.Information);
}