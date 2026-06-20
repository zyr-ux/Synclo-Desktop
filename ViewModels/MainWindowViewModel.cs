using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Factory;
using Synclo.Services.API;
using Synclo.Services.Utilities;

namespace Synclo.ViewModels;

public enum NavigationPage
{
    Home,
    Account,
    Settings,
    About
}

public enum ConnectionStatus
{
    Online,
    Offline,
    NoInternet
}

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IViewModelFactory _factory;
    private readonly INotificationService _notificationService;
    private readonly IAccountService _accountService;
    private readonly IApiService _apiService;
    private readonly IWebSocketService _webSocketService;
    private readonly IDialogService _dialogService;
    
    [ObservableProperty] private ViewModelBase _currentViewModel;
    [ObservableProperty] private ConnectionStatus _connectionStatus = ConnectionStatus.Online;
    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty, 
    NotifyPropertyChangedFor(nameof(IsHomePage)), 
    NotifyPropertyChangedFor(nameof(IsAccountPage)), 
    NotifyPropertyChangedFor(nameof(IsSettingsPage)),
    NotifyPropertyChangedFor(nameof(IsAboutPage))]
    private NavigationPage _currentPage = NavigationPage.Home;
    
    // Computed properties for direct binding without converters
    public bool IsHomePage => CurrentPage == NavigationPage.Home;
    public bool IsAccountPage => CurrentPage == NavigationPage.Account;
    public bool IsSettingsPage => CurrentPage == NavigationPage.Settings;
    public bool IsAboutPage => CurrentPage == NavigationPage.About;
    
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10));
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;

    public MainWindowViewModel(
        IViewModelFactory factory,
        INotificationService notificationService,
        IAccountService accountService,
        IApiService apiService,
        IWebSocketService webSocketService,
        IDialogService dialogService)
    {
        _factory = factory;
        _notificationService = notificationService;
        _accountService = accountService;
        _apiService = apiService;
        _webSocketService = webSocketService;
        _dialogService = dialogService;
        
        IsDialogOpen = _dialogService.IsDialogOpen;
        _dialogService.IsDialogOpenChanged += OnIsDialogOpenChanged;

        CurrentViewModel = _factory.Create<HomeViewModel>();
        CurrentPage = NavigationPage.Home;
        _ = Task.Run(CheckStatus);
    }

    private void OnIsDialogOpenChanged(object? sender, bool isOpen)
    {
        Dispatcher.UIThread.Post(() => IsDialogOpen = isOpen);
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
                ConnectionStatus = ConnectionStatus.NoInternet;
            });
            return;
        }

        try
        {
            await _apiService.Health();
            Dispatcher.UIThread.Post(() =>
            {
                ConnectionStatus = ConnectionStatus.Online;
            });
        }
        catch
        {
            Dispatcher.UIThread.Post(() =>
            {
                ConnectionStatus = ConnectionStatus.Offline;
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
    private void ShowHome()
    {
        CurrentPage = NavigationPage.Home;
        _factory.Release(CurrentViewModel);
        CurrentViewModel = _factory.Create<HomeViewModel>();
    }

    [RelayCommand]
    private void ShowSettings()
    {
        CurrentPage = NavigationPage.Settings;
        _factory.Release(CurrentViewModel);
        CurrentViewModel = _factory.Create<SettingsViewModel>();
    }

    [RelayCommand]
    private void ShowAccount()
    {
        CurrentPage = NavigationPage.Account;
        _factory.Release(CurrentViewModel);
        CurrentViewModel = _factory.Create<AccountViewModel>();
    }

    [RelayCommand]
    private void ShowAbout()
    {
        CurrentPage = NavigationPage.About;
        _factory.Release(CurrentViewModel);
        CurrentViewModel = _factory.Create<AboutViewModel>();
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
        _dialogService.IsDialogOpenChanged -= OnIsDialogOpenChanged;
        _factory.Release(CurrentViewModel);
    }
}