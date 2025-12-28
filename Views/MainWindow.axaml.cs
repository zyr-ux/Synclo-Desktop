using Avalonia.Controls;
using Avalonia.Controls.Notifications;

namespace Synclo.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var manager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 3
        };
        App.APIService.NotificationService.SetManager(manager);
    }
}