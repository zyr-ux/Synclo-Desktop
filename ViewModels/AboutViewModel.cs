using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Utilities;
using Synclo.Features.Connection_Monitor;
using Synclo.Features.Settings_Manager;
using Synclo.Features.Network_Services;
using Synclo.Models;

namespace Synclo.ViewModels;

public partial class AboutViewModel : ViewModelBase, IDisposable
{
    private readonly IUtils _utils;
    private readonly IConnectionMonitor _connectionMonitor;
    private readonly ISettingsService _settingsService;
    private readonly IWebSocketService _webSocketService;

    [ObservableProperty]
    private ConnectionStatus _connectionStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebSocketStatusText))]
    private bool _isWebSocketConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyText))]
    private long? _latencyMs;

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    public string ServerUrl => _settingsService.Settings.ServerUrl;
    public string ServerType => ServerUrl == AppSettings.DefaultServerUrl ? "Public Server" : "Self-hosted Server";
    public string WebSocketStatusText => IsWebSocketConnected ? "Connected" : "Disconnected";
    public string LatencyText => LatencyMs.HasValue ? $"{LatencyMs} ms" : "N/A";
    public string LastSyncText => _settingsService.Settings.last_sync?.ToLocalTime().ToString("yyyy-MM-dd h:mm tt", System.Globalization.CultureInfo.InvariantCulture) ?? "Never";

    public AboutViewModel(
        IUtils utils, 
        IConnectionMonitor connectionMonitor,
        ISettingsService settingsService,
        IWebSocketService webSocketService)
    {
        _utils = utils;
        _connectionMonitor = connectionMonitor;
        _settingsService = settingsService;
        _webSocketService = webSocketService;
        
        ConnectionStatus = _connectionMonitor.ConnectionStatus;
        IsWebSocketConnected = _webSocketService.IsConnected;
        LatencyMs = _connectionMonitor.LatencyMs;

        _connectionMonitor.ConnectionStatusChanged += HandleConnectionStatusChanged;
        _connectionMonitor.LatencyChanged += HandleLatencyChanged;
        _webSocketService.OnConnected += HandleWebSocketConnected;
        _webSocketService.OnDisconnected += HandleWebSocketDisconnected;
        _settingsService.SettingsChanged += HandleSettingsChanged;
    }

    private void HandleConnectionStatusChanged(ConnectionStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = status;
        });
    }

    private void HandleLatencyChanged(long? latencyMs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LatencyMs = latencyMs;
        });
    }

    private void HandleWebSocketConnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsWebSocketConnected = true;
        });
    }

    private void HandleWebSocketDisconnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsWebSocketConnected = false;
        });
    }

    private void HandleSettingsChanged(AppSettings settings)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ServerUrl));
            OnPropertyChanged(nameof(ServerType));
            OnPropertyChanged(nameof(LastSyncText));
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
        _connectionMonitor.LatencyChanged -= HandleLatencyChanged;
        _webSocketService.OnConnected -= HandleWebSocketConnected;
        _webSocketService.OnDisconnected -= HandleWebSocketDisconnected;
        _settingsService.SettingsChanged -= HandleSettingsChanged;
    }
}