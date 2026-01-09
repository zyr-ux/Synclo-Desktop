using System;
using System.Net.NetworkInformation;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Synclo.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly HomeViewModel _homeViewModel = new();
    private readonly SettingsViewModel _settingsViewModel = new();
    private readonly AccountViewModel _accountViewModel = new();

    [ObservableProperty]
    private ViewModelBase _currentViewModel;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private IBrush _statusColor = Brushes.Gray;

    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10));
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;

    public MainWindowViewModel()
    {
        CurrentViewModel = _homeViewModel;
        _ = Task.Run(CheckStatus);
    }

    public async Task InitializeApplicationAsync()
    {
        try
        {
            await App.APIService.AccountService.EnforceLocalKdfVersionAsync();
        }
        catch (SecurityException ex)
        {
            App.NotificationService.ShowError(ex.Message, "Security Update");
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
        _ = App.APIService.WebSocketService.ConnectAsync();

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
            await App.APIService.Health();
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
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("1.1.1.1", 1000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void ShowHome() => CurrentViewModel = _homeViewModel;

    [RelayCommand]
    private void ShowSettings() => CurrentViewModel = _settingsViewModel;

    [RelayCommand]
    private void ShowAccount() => CurrentViewModel = _accountViewModel;

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();

        (_homeViewModel as IDisposable)?.Dispose();
        (_settingsViewModel as IDisposable)?.Dispose();
        (_accountViewModel as IDisposable)?.Dispose();
    }
}