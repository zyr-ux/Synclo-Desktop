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
    private const string BaseUrl = "https://synclo.zyrux.dev";
    private readonly HttpClient _http;
    private readonly ISecureStorage _secureStorage;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _disposed;

    public event Func<CancellationToken, Task<string>>? OnTokenExpired;
    public event Action<string>? TokenRefreshed;

    public APIService(HttpClient http, ISecureStorage secureStorage)
    {
        _http = http;
        _secureStorage = secureStorage;
        
        if (_http.BaseAddress == null)
        {
            _http.BaseAddress = new Uri(BaseUrl.TrimEnd('/'));
            _http.Timeout = TimeSpan.FromSeconds(15);
        }
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
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

        if (OnTokenExpired == null)
            throw new InvalidOperationException("No token refresh handler configured");

        await _refreshLock.WaitAsync(ct);
        try
        {
            var newToken = await OnTokenExpired.Invoke(ct);
            TokenRefreshed?.Invoke(newToken);
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

        var token = await _secureStorage.LoadAsync(AccountService.AccessToken);
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
        _http.Dispose();
        _refreshLock.Dispose();
    }

    #endregion
}