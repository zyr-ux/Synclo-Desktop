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
    long? LatencyMs { get; }
    event Action<ConnectionStatus>? ConnectionStatusChanged;
    event Action<long?>? LatencyChanged;
    Task CheckStatusAsync();
}

public sealed class ConnectionMonitor : IConnectionMonitor
{
    private readonly IApiService _apiService;
    private readonly IWebSocketService _webSocketService;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10));
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollingTask;

    private ConnectionStatus _connectionStatus = ConnectionStatus.Online;
    private long? _latencyMs;
    private bool _useWebSocketLatency;
    public ConnectionStatus ConnectionStatus => _connectionStatus;
    public long? LatencyMs => _latencyMs;
    public event Action<ConnectionStatus>? ConnectionStatusChanged;
    public event Action<long?>? LatencyChanged;

    public ConnectionMonitor(IApiService apiService, IWebSocketService webSocketService)
    {
        _apiService = apiService;
        _webSocketService = webSocketService;

        _webSocketService.LatencyChanged += HandleWebSocketLatency;
        _webSocketService.OnConnected += HandleWebSocketConnected;
        _webSocketService.OnDisconnected += HandleWebSocketDisconnected;

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
        long? targetLatency = null;
        if (!await CheckInternet())
        {
            targetStatus = ConnectionStatus.NoInternet;
        }
        else
        {
            try
            {
                if (_useWebSocketLatency)
                {
                    // WS is handling latency; just verify server reachability
                    await _apiService.Health();
                }
                else
                {
                    // No WS connection — measure HTTP round-trip as fallback latency
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    await _apiService.Health();
                    stopwatch.Stop();
                    targetLatency = stopwatch.ElapsedMilliseconds;
                }
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

        // Only update latency from HTTP when WS isn't providing it
        if (!_useWebSocketLatency)
        {
            var latencyChanged = _latencyMs != targetLatency;
            _latencyMs = targetLatency;
            if (latencyChanged)
            {
                LatencyChanged?.Invoke(_latencyMs);
            }
        }
    }

    private void HandleWebSocketLatency(long? latencyMs)
    {
        if (latencyMs == null)
        {
            _useWebSocketLatency = false;
        }

        var changed = _latencyMs != latencyMs;
        _latencyMs = latencyMs;
        if (changed)
        {
            LatencyChanged?.Invoke(_latencyMs);
        }
    }

    private void HandleWebSocketConnected()
    {
        _useWebSocketLatency = true;
    }

    private void HandleWebSocketDisconnected()
    {
        _useWebSocketLatency = false;
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
        _webSocketService.LatencyChanged -= HandleWebSocketLatency;
        _webSocketService.OnConnected -= HandleWebSocketConnected;
        _webSocketService.OnDisconnected -= HandleWebSocketDisconnected;

        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }
}
