using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Features.Network_Services;

namespace Synclo.Features.Connection_Monitor;

public enum ConnectionStatus
{
    Online,
    Offline,
    NoInternet
}

public interface IConnectionMonitor : IDisposable
{
    ConnectionStatus ConnectionStatus { get; }
    event Action<ConnectionStatus>? ConnectionStatusChanged;
    Task CheckStatusAsync();
}

public sealed class ConnectionMonitor : IConnectionMonitor
{
    private readonly IApiService _apiService;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10));
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollingTask;

    private ConnectionStatus _connectionStatus = ConnectionStatus.Online;
    public ConnectionStatus ConnectionStatus => _connectionStatus;
    public event Action<ConnectionStatus>? ConnectionStatusChanged;

    public ConnectionMonitor(IApiService apiService)
    {
        _apiService = apiService;
        _pollingTask = PollingLoop();
    }

    private async Task PollingLoop()
    {
        try
        {
            // Initial check on startup
            _ = Task.Run(CheckStatusAsync);

            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                _ = Task.Run(CheckStatusAsync);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task CheckStatusAsync()
    {
        ConnectionStatus targetStatus;
        if (!await CheckInternet())
        {
            targetStatus = ConnectionStatus.NoInternet;
        }
        else
        {
            try
            {
                await _apiService.Health();
                targetStatus = ConnectionStatus.Online;
            }
            catch
            {
                targetStatus = ConnectionStatus.Offline;
            }
        }

        if (_connectionStatus != targetStatus)
        {
            _connectionStatus = targetStatus;
            ConnectionStatusChanged?.Invoke(_connectionStatus);
        }
    }

    private static async Task<bool> CheckInternet()
    {
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

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }
}
