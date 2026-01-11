using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Synclo.Services;

namespace Synclo.Views;

public partial class MainWindow : Window
{
    public MainWindow(NotificationService notificationService)
    {
        InitializeComponent();

        var manager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 3
        };
        notificationService.SetManager(manager);
    }
}