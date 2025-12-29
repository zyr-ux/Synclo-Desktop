using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Services;

namespace Synclo.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AccountViewModel _accountViewModel = new();
    private readonly HomeViewModel _homeViewModel = new();
    private readonly SettingsViewModel _settingsViewModel = new();

    [ObservableProperty] private ViewModelBase _currentViewModel;
    
    [ObservableProperty] private string _statusText = "Unknown";
    [ObservableProperty] private IBrush _statusColor = Brushes.LightGray;
    
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10));
    private CancellationTokenSource _cts = new();

    public MainWindowViewModel()
    {
        CurrentViewModel = _homeViewModel;
        _ = CheckStatus();
        _ = PollingLoop();
    }

    [RelayCommand]
    private void ShowHome()
    {
        CurrentViewModel = _homeViewModel;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        CurrentViewModel = _settingsViewModel;
    }

    [RelayCommand]
    private void  ShowAccount()
    {
        CurrentViewModel = _accountViewModel;
    }
    
    private async Task PollingLoop()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
                await CheckStatus();
        }
        catch (OperationCanceledException) { }
    }
    
    private async Task CheckStatus()
    {
        if (!await CheckInternet())
        {
            StatusText = "No Internet";
            StatusColor = Brushes.Orange;
            return;
        }

        try
        {
            await App.APIService.Health();
            StatusText = "Online";
            StatusColor = Brushes.Lime;
        }
        catch
        {
            StatusText = "Offline";
            StatusColor = Brushes.Red;
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
    
    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }
    
    
}