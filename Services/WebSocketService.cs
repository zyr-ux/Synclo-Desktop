using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Services.SecretsManager;

namespace Synclo.Services;

public interface IWebSocketService : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync();
    Task DisconnectAsync();
    Task<bool> EnsureConnectedAsync(TimeSpan timeout);
    Task SendMessageAsync<T>(T message);
    Task SendAsync(string text);
    event Action<string>? OnMessageReceived;
    event Action? OnConnected;
    event Action? OnDisconnected;
    event Action<string>? OnError;
}

public sealed class WebSocketService : IWebSocketService
{
    private readonly IApiService _apiService;
    private readonly ISecureStorage _secureStorage;
    private readonly IRefreshTokenService _refreshTokenService;
    private const int BufferSize = 8192;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _manualDisconnect;
    private int _retryCount;

    private ClientWebSocket? _socket;
    private Task? _pingTask;

    public WebSocketService(IApiService api, ISecureStorage secureStorage, IRefreshTokenService refreshTokenService)
    {
        _apiService = api;
        _secureStorage = secureStorage;
        _refreshTokenService = refreshTokenService;
        _refreshTokenService.TokenRefreshed += OnTokenRefreshed;
    }

    public bool IsConnected => _socket is { State: WebSocketState.Open };
    public event Action<string>? OnMessageReceived;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;

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

    /// <summary>
    /// Ensures WebSocket is connected within the specified timeout.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for connection</param>
    /// <returns>True if connected, false if timeout</returns>
    public async Task<bool> EnsureConnectedAsync(TimeSpan timeout)
    {
        if (IsConnected) return true;
        
        await ConnectAsync();
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!IsConnected && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(100);
        }
        
        return IsConnected;
    }

    /// <summary>
    /// Sends a message with automatic JSON serialization.
    /// </summary>
    /// <typeparam name="T">Type of message to send</typeparam>
    /// <param name="message">Message object to serialize and send</param>
    public async Task SendMessageAsync<T>(T message)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(message);
        await SendAsync(json);
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

        // Use Authorization header instead of query parameter
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        var url = "wss://synclo.zyrux.dev/ws/clipboard";

        try
        {
            await _socket.ConnectAsync(new Uri(url), _cts.Token);
            _retryCount = 0;
            _ = ReceiveLoop();
            _pingTask = StartPingLoop(_cts.Token);
            OnConnected?.Invoke();
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.NotAWebSocket || ex.Message.Contains("403"))
        {
            await Handle403AndRetry();
        }
        catch
        {
            _ = ScheduleReconnectBackground();
        }
    }

    private async Task Handle403AndRetry()
    {
        try
        {
            // Use centralized token refresh to prevent concurrent refresh attempts
            await _refreshTokenService.RefreshAsync(_cts?.Token ?? CancellationToken.None);

            var newToken = await _secureStorage.LoadAsync(AccountService.AccessToken);
            if (string.IsNullOrWhiteSpace(newToken)) return;

            await DisconnectInternal();

            _socket = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            // Use Authorization header instead of query parameter
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {newToken}");
            var url = "wss://synclo.zyrux.dev/ws/clipboard";
            await _socket.ConnectAsync(new Uri(url), _cts.Token);
            _retryCount = 0;
            _ = ReceiveLoop();
            _pingTask = StartPingLoop(_cts.Token);
            OnConnected?.Invoke();
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
                
                // Handle protocol messages (ping/pong/error) before passing to subscribers
                if (await HandleProtocolMessage(message))
                {
                    continue; // Protocol message handled, don't invoke OnMessageReceived
                }
                
                // Only invoke for actual data messages
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

    /// <summary>
    /// Handles WebSocket protocol messages (ping, pong, error).
    /// </summary>
    /// <param name="message">The received message</param>
    /// <returns>True if message was a protocol message and was handled, false otherwise</returns>
    private async Task<bool> HandleProtocolMessage(string message)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("type", out var typeProperty))
            {
                var messageType = typeProperty.GetString();
                
                switch (messageType)
                {
                    case "ping":
                        // Respond to server ping to keep connection alive
                        await SendAsync("{\"type\":\"pong\"}");
                        return true;
                    
                    case "pong":
                        // Ignore pong responses (already tracked by ping loop)
                        return true;
                    
                    case "error":
                        // Extract and propagate error message
                        var errorMsg = doc.RootElement.TryGetProperty("message", out var msgProp)
                            ? msgProp.GetString() ?? "Unknown error"
                            : "Unknown error";
                        OnError?.Invoke(errorMsg);
                        return true;
                }
            }
        }
        catch
        {
            // Not a valid JSON or doesn't have type field - treat as data message
        }
        
        return false; // Not a protocol message
    }

    private async Task HandleClose(WebSocketCloseStatus? status)
    {
        if (_disposed) return;

        await DisconnectInternal();
        OnDisconnected?.Invoke();

        if (_manualDisconnect) return;

        if (status is WebSocketCloseStatus.PolicyViolation)
        {
            await Handle403AndRetry();
            return;
        }

        if (status is WebSocketCloseStatus.ProtocolError)
        {
            _ = ScheduleReconnectBackground();
            return;
        }

        if (status.HasValue)
        {
            var code = (int)status.Value;
            if (code == 4001)
            {
                try
                {
                    // Use centralized token refresh to prevent concurrent refresh attempts
                    await _refreshTokenService.RefreshAsync(_cts?.Token ?? CancellationToken.None);
                }
                catch
                {
                    // If refresh fails, schedule normal reconnect (which may lead to logout)
                }

                _ = ScheduleReconnectBackground();
                return;
            }

            if (code == 4002)
            {
                _ = ScheduleReconnectBackground();
                return;
            }

            if (code == 1008)
            {
                await Handle403AndRetry();
                return;
            }
        }
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
        var wasConnected = IsConnected;
        
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

            if (_pingTask != null)
            {
                try { await _pingTask; } catch { }
                _pingTask = null;
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
            
            if (wasConnected && !_manualDisconnect)
            {
                OnDisconnected?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _refreshTokenService.TokenRefreshed -= OnTokenRefreshed;

        _cts?.Cancel();
        _ = DisconnectInternal();

        _connectLock.Dispose();
    }
    
    private Task StartPingLoop(CancellationToken token)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    if (token.IsCancellationRequested) break;
                    if (IsConnected)
                    {
                        try
                        {
                            await SendAsync("{\"type\":\"ping\"}");
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }, token);
    }
    
    #endregion
}