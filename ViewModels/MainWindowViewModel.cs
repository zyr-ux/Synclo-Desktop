using System;
using System.Net.Http;
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
        try
        {
            await App.APIService.Health();
            StatusText = "Online";
            StatusColor = Brushes.Lime;
        }
        catch (HttpRequestException)
        {
            StatusText = "Unreachable";
            StatusColor = Brushes.LightGray;
        }
        catch
        {
            StatusText  = "Offline";
            StatusColor = Brushes.Red;
        }
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }
    
    
}