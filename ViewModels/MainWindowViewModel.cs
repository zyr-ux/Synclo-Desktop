using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Factory;
using Synclo.Services;

namespace Synclo.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IViewModelFactory _factory;
    private readonly NotificationService _notificationService;
    private readonly AccountService _accountService;
    private readonly APIService _apiService;
    private readonly WebSocketService _webSocketService;
    [ObservableProperty] private ViewModelBase _currentViewModel;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private IBrush _statusColor = Brushes.Gray;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10));
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;

    public MainWindowViewModel(
        IViewModelFactory factory,
        NotificationService notificationService,
        AccountService accountService,
        APIService apiService,
        WebSocketService webSocketService)
    {
        _factory = factory;
        _notificationService = notificationService;
        _accountService = accountService;
        _apiService = apiService;
        _webSocketService = webSocketService;
        CurrentViewModel = _factory.Create<HomeViewModel>();
        _ = Task.Run(CheckStatus);
    }

    public async Task InitializeApplicationAsync()
    {
        try
        {
            await _accountService.EnforceLocalKdfVersionAsync();
        }
        catch (SecurityException ex)
        {
            _notificationService.ShowError(ex.Message, "Security Update");
            return;
        }
        catch
        {
            return;
        }

        StartBackgroundServices();
    }

    private void StartBackgroundServices()
    {
        _ = _webSocketService.ConnectAsync();

        if (_pollingTask == null)
            _pollingTask = PollingLoop();
    }

    private async Task PollingLoop()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                _ = Task.Run(CheckStatus);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
    
    private async Task CheckStatus()
    {
        if (!await CheckInternet())
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = "No Internet";
                StatusColor = Brushes.Orange;
            });
            return;
        }

        try
        {
            await _apiService.Health();
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = "Online";
                StatusColor = Brushes.Lime;
            });
        }
        catch
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = "Server Offline";
                StatusColor = Brushes.Red;
            });
        }
    }

    private static async Task<bool> CheckInternet()
    {
        // Try ICMP ping first (fastest when not blocked)
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("1.1.1.1", 1000);
            if (reply.Status == IPStatus.Success)
                return true;
        }
        catch
        {
            // Ping may be blocked, fall through to HTTP check
        }
        
        // Fallback: HTTP check for networks that block ICMP
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync("https://www.google.com/generate_204");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void ShowHome() => SwitchTo<HomeViewModel>();

    [RelayCommand]
    private void ShowSettings() => SwitchTo<SettingsViewModel>();

    [RelayCommand]
    private void ShowAccount() => SwitchTo<AccountViewModel>();

    private void SwitchTo<T>() where T : ViewModelBase
    {

        CurrentViewModel = _factory.Create<T>();
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();

        (CurrentViewModel as IDisposable)?.Dispose();
    }
}