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
using Synclo.Services.SecretsManager;
using Synclo.Models;

namespace Synclo.ViewModels;

public enum NavigationPage
{
    Home,
    Account,
    Settings,
    About
}


public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IViewModelFactory _factory;
    private readonly INotificationService _notificationService;
    private readonly IAccountService _accountService;
    private readonly IWebSocketService _webSocketService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly ISecretsManager _secretsManager;
    
    [ObservableProperty] private ViewModelBase _currentViewModel;
    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty, 
    NotifyPropertyChangedFor(nameof(IsHomePage)), 
    NotifyPropertyChangedFor(nameof(IsAccountPage)), 
    NotifyPropertyChangedFor(nameof(IsSettingsPage)),
    NotifyPropertyChangedFor(nameof(IsAboutPage))]
    private NavigationPage _currentPage = NavigationPage.Home;
    
    [ObservableProperty,
    NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _isSidebarCollapsed;
    
    public double SidebarWidth => IsSidebarCollapsed ? 54 : 200;
    
    // Computed properties for direct binding without converters
    public bool IsHomePage => CurrentPage == NavigationPage.Home;
    public bool IsAccountPage => CurrentPage == NavigationPage.Account;
    public bool IsSettingsPage => CurrentPage == NavigationPage.Settings;
    public bool IsAboutPage => CurrentPage == NavigationPage.About;
    
    public MainWindowViewModel(
        IViewModelFactory factory,
        INotificationService notificationService,
        IAccountService accountService,
        IWebSocketService webSocketService,
        IDialogService dialogService,
        ISettingsService settingsService,
        ISecretsManager secretsManager)
    {
        _factory = factory;
        _notificationService = notificationService;
        _accountService = accountService;
        _webSocketService = webSocketService;
        _dialogService = dialogService;
        _settingsService = settingsService;
        _secretsManager = secretsManager;
        
        IsDialogOpen = _dialogService.IsDialogOpen;
        _dialogService.IsDialogOpenChanged += OnIsDialogOpenChanged;

        CurrentViewModel = _factory.Create<HomeViewModel>();
        CurrentPage = NavigationPage.Home;
        _isSidebarCollapsed = _settingsService.Settings.is_sidebar_collapsed;
    }

    private void OnIsDialogOpenChanged(object? sender, bool isOpen)
    {
        Dispatcher.UIThread.Post(() => IsDialogOpen = isOpen);
    }

    public async Task InitializeApplicationAsync()
    {
        try
        {
            var serverUrl = await _secretsManager.GetServerUrlAsync();
            if (!string.IsNullOrEmpty(serverUrl))
            {
                _settingsService.Settings.ServerUrl = serverUrl;
            }
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

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        _settingsService.Settings.is_sidebar_collapsed = IsSidebarCollapsed;
        _settingsService.Save();
    }
    
    public void Dispose()
    {
        _dialogService.IsDialogOpenChanged -= OnIsDialogOpenChanged;
        _factory.Release(CurrentViewModel);
    }
}