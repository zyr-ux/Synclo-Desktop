using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Synclo.Factory;
using Synclo.Services;
using Synclo.Services.API;

namespace Synclo.ViewModels;

public partial class AccountViewModel : ViewModelBase, IDisposable
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
        _accountService.OnLogout += OnLogoutAsync;
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
        _factory.Release(CurrentViewModel);
        var loginViewModel = _factory.Create<LoginViewModel>();
        loginViewModel.LoginSucceeded += OnLoginSuccess;
        CurrentViewModel = loginViewModel;
    }

    private void ShowAccountDetails() {
        _factory.Release(CurrentViewModel);
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

    private async Task OnLogoutAsync()
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(ShowLogin);
    }

    public void Dispose()
    {
        _accountService.OnLogout -= OnLogoutAsync;
        _factory.Release(CurrentViewModel);
    }
}