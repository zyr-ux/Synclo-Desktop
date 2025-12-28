using Avalonia.Controls.Notifications;

namespace Synclo.Services;

/// <summary>
/// Centralized notification service using Avalonia's WindowNotificationManager.
/// </summary>
public sealed class NotificationService
{
    private INotificationManager? _manager;

    public void SetManager(INotificationManager manager)
    {
        _manager = manager;
    }

    public void ShowInfo(string message, string? title = null)
    {
        _manager?.Show(new Notification(title ?? "Info", message, NotificationType.Information));
    }

    public void ShowSuccess(string message, string? title = null)
    {
        _manager?.Show(new Notification(title ?? "Success", message, NotificationType.Success));
    }

    public void ShowWarning(string message, string? title = null)
    {
        _manager?.Show(new Notification(title ?? "Warning", message, NotificationType.Warning));
    }

    public void ShowError(string message, string? title = null)
    {
        _manager?.Show(new Notification(title ?? "Error", message, NotificationType.Error));
    }
}

