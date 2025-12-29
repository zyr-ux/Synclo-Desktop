using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

public sealed class APIService : IDisposable
{
    public const string BaseUrl = "https://synclo.zyrux.dev";
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _disposed;
    
    public AccountService AccountService { get; }
    public AuthService AuthService { get; }
    public CryptographyService CryptographyService { get; }
    public DeviceService DeviceService { get; }
    public DeviceCacheService DeviceCacheService { get; }
    public WebSocketService WebSocketService { get; }
    public NotificationService NotificationService { get; }
    public ClipboardService ClipboardService { get; }

    public event Action<string>? TokenRefreshed;

    public APIService(ISettingsService settings)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(15)
        };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // initialize the various sub-services
        AuthService = new AuthService(this, _http);
        CryptographyService = new CryptographyService();
        DeviceService = new DeviceService(this);
        DeviceCacheService = new DeviceCacheService();
        WebSocketService = new WebSocketService(this);
        AccountService = new AccountService(this, settings, DeviceCacheService);
        NotificationService = new NotificationService();
        ClipboardService = new ClipboardService(this);
    }
    
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
    {
        return SendAuthReqAsync(HttpMethod.Get, url, null, ct);
    }

    public Task<HttpResponseMessage> PostAsync(string url, object body, CancellationToken ct = default)
    {
        return SendAuthReqAsync(HttpMethod.Post, url, body, ct);
    }

    public Task<HttpResponseMessage> DeleteAsync(string url, CancellationToken ct = default)
    {
        return SendAuthReqAsync(HttpMethod.Delete, url, null, ct);
    }

    public async Task Health()
    {
        var req = await _http.GetAsync("/api/health");
        req.EnsureSuccessStatusCode();
    }

    #region Helper Proxies

    private async Task<HttpResponseMessage> SendAuthReqAsync(HttpMethod method, string url, object? body,
        CancellationToken ct)
    {
        var response = await SendReqHelper(method, url, body, ct);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();

        await _refreshLock.WaitAsync(ct);
        try
        {
            var newToken = await AuthService.RefreshTokenAsyncInt(ct);
            // Notify the rest of the app (e.g., WebSockets) that the token changed
            TokenRefreshed?.Invoke(newToken);
            // Retry the original request with the new token
            return await SendReqHelper(method, url, body, ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendReqHelper(HttpMethod method, string url, object? body,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);

        var token = await SecureStorage.LoadAsync(AuthService.AccessToken);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body != null)
            req.Content = Serialize(body);

        try
        {
            var res = await _http.SendAsync(req, ct);

            if ((int)res.StatusCode >= 500)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                res.Dispose();
                throw new ServerFailureException(text);
            }

            return res;
        }
        catch (HttpRequestException)
        {
            throw new NetworkFailureException();
        }
    }

    public StringContent Serialize(object obj)
    {
        return new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");
    }

    public T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, _jsonOptions)!;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        WebSocketService.Dispose();
        _http.Dispose();
        _refreshLock.Dispose();
    }

    #endregion
}