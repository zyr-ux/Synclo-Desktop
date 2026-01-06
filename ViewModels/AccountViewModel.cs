using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Synclo.Services;

namespace Synclo.ViewModels;

public partial class AccountViewModel : ViewModelBase
{
    private readonly AccountService _accountService;
    private readonly DeviceService _deviceService;

    [ObservableProperty] private ViewModelBase? _currentViewModel;

    public AccountViewModel()
    {
        _accountService = App.APIService.AccountService;
        _deviceService = App.APIService.DeviceService;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (await _accountService.IsAuthenticatedAsync())
        {
            var email = await _accountService.GetStoredEmailAsync() ?? "";
            ShowAccountManagement(email);
            _ = App.APIService.WebSocketService.ConnectAsync();
        }
        else
        {
            ShowLogin();
        }
    }

    private void ShowLogin()
    {
        CurrentViewModel = new LoginViewModel(_accountService, OnLoginSuccess);
    }

    private void ShowAccountManagement(string email)
    {
        CurrentViewModel = new AccountDetailsViewModel(_accountService, _deviceService, email, OnLogout);
    }

    private void OnLoginSuccess(string email)
    {
        ShowAccountManagement(email);
        _ = App.APIService.WebSocketService.ConnectAsync();
    }

    private void OnLogout()
    {
        ShowLogin();
    }
}