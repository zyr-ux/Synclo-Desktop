using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Services.API;
using Synclo.Services.Utilities;

namespace Synclo.ViewModels;

public enum ConnectionStatus
{
    Online,
    Offline,
    NoInternet
}

public partial class AboutViewModel : ViewModelBase, IDisposable
{
    private readonly IUtils _utils;
    private readonly IApiService _apiService;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10));
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;

    [ObservableProperty]
    private ConnectionStatus _connectionStatus = ConnectionStatus.Online;

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public AboutViewModel(IUtils utils, IApiService apiService)
    {
        _utils = utils;
        _apiService = apiService;
        _ = Task.Run(CheckStatus);
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
    private void CheckStatusManual()
    {
        _ = Task.Run(CheckStatus);
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        _utils.OpenUrl("https://github.com/zyr-ux/Synclo-Desktop");
    }

    [RelayCommand]
    private void OpenIssues()
    {
        _utils.OpenUrl("https://github.com/zyr-ux/Synclo-Desktop/issues");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }
}