using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Synclo.Factory;
using Synclo.Services;

namespace Synclo.ViewModels;

public partial class AccountViewModel : ViewModelBase
{
    private readonly IViewModelFactory _factory;
    private readonly AccountService _accountService;
    private readonly WebSocketService _webSocketService;

    [ObservableProperty] private ViewModelBase? _currentViewModel;

    public AccountViewModel(
        IViewModelFactory factory,
        AccountService accountService,
        WebSocketService webSocketService)
    {
        _factory = factory;
        _accountService = accountService;
        _webSocketService = webSocketService;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (await _accountService.IsAuthenticatedAsync())
        {
            ShowAccountDetails();
            _ = _webSocketService.ConnectAsync();
        }
        else
        {
            ShowLogin();
        }
    }

    private void ShowLogin()
    {
        var loginViewModel = _factory.Create<LoginViewModel>();
        loginViewModel.LoginSucceeded += OnLoginSuccess;
        CurrentViewModel = loginViewModel;
    }

    private void ShowAccountDetails() {
        var accountDetailsViewModel = _factory.Create<AccountDetailsViewModel>();
        accountDetailsViewModel.LoggedOut += OnLogout;
        CurrentViewModel = accountDetailsViewModel;
    }

    private void OnLoginSuccess()
    {
        ShowAccountDetails();
        _ = _webSocketService.ConnectAsync();
    }

    private void OnLogout()
    {
        ShowLogin();
    }
}