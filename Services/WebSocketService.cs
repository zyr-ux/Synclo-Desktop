using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Synclo.SecretsManager;

namespace Synclo.Services;

public sealed class WebSocketService : IDisposable
{
    private readonly APIService APIService;
    private readonly ISecureStorage _secureStorage;
    private const int BufferSize = 8192;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _manualDisconnect;
    private int _retryCount;

    private ClientWebSocket? _socket;

    public WebSocketService(APIService api, ISecureStorage secureStorage)
    {
        APIService = api;
        _secureStorage = secureStorage;
        APIService.TokenRefreshed += OnTokenRefreshed;
    }

    public bool IsConnected => _socket is { State: WebSocketState.Open };
    public event Action<string>? OnMessageReceived;
    public event Action? OnDisconnected;

    private void OnTokenRefreshed(string token)
    {
        if (_disposed || _manualDisconnect) return;
        _ = ScheduleReconnectBackground();
    }

    public async Task ConnectAsync()
    {
        if (_disposed) return;

        _manualDisconnect = false;

        await _connectLock.WaitAsync();
        try
        {
            await ConnectInternal();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_disposed) return;

        _manualDisconnect = true;
        _retryCount = 0;

        await DisconnectInternal();
    }

    #region Internal Methods

    private async Task ConnectInternal()
    {
        if (_disposed || _manualDisconnect || IsConnected) return;

        var token = await _secureStorage.LoadAsync(AccountService.AccessToken);
        if (string.IsNullOrWhiteSpace(token)) return;

        await DisconnectInternal();

        _socket = new ClientWebSocket();
        _cts = new CancellationTokenSource();

        var url = $"wss://synclo.zyrux.dev/api/ws/clipboard?token={token}";

        try
        {
            await _socket.ConnectAsync(new Uri(url), _cts.Token);
            _retryCount = 0;
            _ = ReceiveLoop();
        }
        catch
        {
            _ = ScheduleReconnectBackground();
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[BufferSize];

        try
        {
            while (!_disposed && !_manualDisconnect && IsConnected && _cts != null)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket!.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await HandleClose(result.CloseStatus);
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var message = Encoding.UTF8.GetString(ms.ToArray());
                OnMessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            // normal exit on disconnect
        }
        catch
        {
            _ = ScheduleReconnectBackground();
        }
    }

    private async Task HandleClose(WebSocketCloseStatus? status)
    {
        if (_disposed) return;

        await DisconnectInternal();

        if (_manualDisconnect) return;

        if (status is WebSocketCloseStatus.PolicyViolation or WebSocketCloseStatus.ProtocolError)
        {
            _ = ScheduleReconnectBackground();
            return;
        }

        OnDisconnected?.Invoke();
    }

    public async Task SendAsync(string text)
    {
        if (_disposed || _manualDisconnect || _cts == null) return;

        var ws = _socket;
        if (ws == null || ws.State != WebSocketState.Open) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
        catch
        {
            _ = ScheduleReconnectBackground();
        }
    }

    private Task ScheduleReconnectBackground()
    {
        return Task.Run(async () => await ScheduleReconnect());
    }

    private async Task ScheduleReconnect()
    {
        if (_disposed || _manualDisconnect) return;
        if (!await _connectLock.WaitAsync(0)) return;

        try
        {
            if (_disposed || _manualDisconnect || IsConnected) return;

            await DisconnectInternal();

            var delay = Math.Min(1000 << Math.Min(_retryCount, 15), 30000);
            await Task.Delay(delay);

            if (_disposed || _manualDisconnect) return;

            _retryCount = Math.Min(_retryCount + 1, 15);

            await ConnectInternal();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task DisconnectInternal()
    {
        try
        {
            _cts?.Cancel();

            if (_socket != null)
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", timeout.Token);
                    }
                    catch
                    {
                    }
                }

                _socket.Dispose();
            }
        }
        catch
        {
        }
        finally
        {
            _socket = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        APIService.TokenRefreshed -= OnTokenRefreshed;

        _cts?.Cancel();
        _ = DisconnectInternal();

        _connectLock.Dispose();
    }

    #endregion
}