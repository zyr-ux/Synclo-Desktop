using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Utilities;
using Synclo.Features.Connection_Monitor;

namespace Synclo.ViewModels;

public partial class AboutViewModel : ViewModelBase, IDisposable
{
    private readonly IUtils _utils;
    private readonly IConnectionMonitor _connectionMonitor;

    [ObservableProperty]
    private ConnectionStatus _connectionStatus;

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public AboutViewModel(IUtils utils, IConnectionMonitor connectionMonitor)
    {
        _utils = utils;
        _connectionMonitor = connectionMonitor;
        
        ConnectionStatus = _connectionMonitor.ConnectionStatus;
        _connectionMonitor.ConnectionStatusChanged += HandleConnectionStatusChanged;
    }

    private void HandleConnectionStatusChanged(ConnectionStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = status;
        });
    }

    [RelayCommand]
    private void CheckStatusManual()
    {
        _ = Task.Run(() => _connectionMonitor.CheckStatusAsync());
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        _utils.OpenUrl("https://github.com/zyr-ux/Synclo-Desktop");
    }

    [RelayCommand]
    private void OpenIssues()
    {
        _utils.OpenUrl("https://github.com/zyr-ux/Synclo-Desktop/issues");
    }

    public void Dispose()
    {
        _connectionMonitor.ConnectionStatusChanged -= HandleConnectionStatusChanged;
    }
}