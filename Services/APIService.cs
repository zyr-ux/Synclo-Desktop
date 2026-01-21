using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Polly;
using Synclo.Services.SecretsManager;

namespace Synclo.Services;

public interface IApiService : IDisposable
{
    Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default);
    Task<HttpResponseMessage> PostAsync(string url, object body, CancellationToken ct = default);
    Task<HttpResponseMessage> DeleteAsync(string url, CancellationToken ct = default);
    Task Health();
    StringContent Serialize(object obj);
    T Deserialize<T>(string json);
}

public sealed class ApiService : IApiService
{
    private const string BaseUrl = "https://synclo.zyrux.dev";
    private readonly HttpClient _http;
    private readonly ISecureStorage _secureStorage;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public ApiService(HttpClient http, ISecureStorage secureStorage, IRefreshTokenService refreshTokenService)
    {
        _http = http;
        _secureStorage = secureStorage;
        _refreshTokenService = refreshTokenService;
        
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

        // Trigger refresh and wait for result
        _refreshTokenService.RaiseTokenExpired();
        var result = await _refreshTokenService.WaitForRefreshAsync(ct);
        
        if (result == AuthRefreshResult.SessionExpired)
            throw new SessionExpiredException();
        
        if (result == AuthRefreshResult.NetworkFailure)
            throw new NetworkFailureException();
        
        // Success - retry request with new token
        return await SendReqHelper(method, url, body, ct);
    }

    private async Task<HttpResponseMessage> SendReqHelper(HttpMethod method, string url, object? body,
        CancellationToken ct)
    {
        try
        {
            var policy = Policy.HandleResult<HttpResponseMessage>(r => (int)r.StatusCode == 429)
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, retryAttempt - 1), 30000)),
                    onRetry: (_, _, _, _) => { });

            var res = await policy.ExecuteAsync(async (ct2) =>
            {
                using var attemptReq = new HttpRequestMessage(method, url);
                var token2 = await _secureStorage.LoadAsync(AccountService.AccessToken);
                if (!string.IsNullOrWhiteSpace(token2))
                    attemptReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token2);

                if (body != null)
                    attemptReq.Content = Serialize(body);

                return await _http.SendAsync(attemptReq, ct2).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if ((int)res.StatusCode == 429)
            {
                var retryAfter = ParseRetryAfter(res);
                var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                res.Dispose();
                throw new RateLimitException($"Rate limited. Retry after: {retryAfter.TotalSeconds}s. Response: {text}");
            }

            if ((int)res.StatusCode >= 500)
            {
                var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

    private static TimeSpan ParseRetryAfter(HttpResponseMessage res)
    {
        if (res.Headers.RetryAfter != null)
        {
            if (res.Headers.RetryAfter.Delta.HasValue)
                return res.Headers.RetryAfter.Delta.Value;
            if (res.Headers.RetryAfter.Date.HasValue)
            {
                var date = res.Headers.RetryAfter.Date.Value;
                var delta = date - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }
        }
        // Default backoff
        return TimeSpan.FromSeconds(30);
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
    }

    #endregion
}