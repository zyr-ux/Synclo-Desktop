using System;
using Avalonia.Controls;
using Synclo.Services;

namespace Synclo.Views;

public partial class MainWindow : Window
{
    private IApplicationControlService? _appControl;
    private bool _isExplicitShutdown;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void Initialize(IApplicationControlService appControl)
    {
        _appControl = appControl;
        _appControl.ShutdownRequested += OnShutdownRequested;
    }

    private void OnShutdownRequested()
    {
        _isExplicitShutdown = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // If explicit shutdown (from tray Quit), allow close
        if (_isExplicitShutdown)
        {
            base.OnClosing(e);
            return;
        }

        // If settings say minimize on close, hide instead
        if (_appControl?.ShouldMinimizeOnClose() == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_appControl != null)
        {
            _appControl.ShutdownRequested -= OnShutdownRequested;
        }

        base.OnClosed(e);

        if (!_isExplicitShutdown)
        {
            _appControl?.Shutdown();
        }
    }
}
