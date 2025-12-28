using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Synclo.Services;

namespace Synclo.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var manager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3
        };
        Synclo.App.APIService.NotificationService.SetManager(manager);
    }
}