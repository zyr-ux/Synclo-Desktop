using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Synclo.Factory;
using Synclo.Services;

namespace Synclo.ViewModels;

public partial class AccountViewModel : ViewModelBase
{
    private readonly IViewModelFactory _factory;
    private readonly IAccountService _accountService;

    [ObservableProperty] private ViewModelBase? _currentViewModel;

    public AccountViewModel(
        IViewModelFactory factory,
        IAccountService accountService)
    {
        _factory = factory;
        _accountService = accountService;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (await _accountService.IsAuthenticatedAsync())
        {
            ShowAccountDetails();
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
    }

    private void OnLogout()
    {
        ShowLogin();
    }
}