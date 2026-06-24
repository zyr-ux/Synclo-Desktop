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
using Synclo.Services.Utilities;

namespace Synclo.Services.API;

public interface IApiService : IDisposable
{
    Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default);
    Task<HttpResponseMessage> PostAsync(string url, object body, CancellationToken ct = default);
    Task<HttpResponseMessage> DeleteAsync(string url, CancellationToken ct = default);
    Task Health(string? baseUrl = null);
    StringContent Serialize(object obj);
    T Deserialize<T>(string json);
    string GetAbsoluteUrl(string url);
}

public sealed class ApiService : IApiService
{
    private readonly HttpClient _http;
    private readonly ISecretsManager _secretsManager;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly ISettingsService _settingsService;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public ApiService(
        HttpClient http, 
        ISecretsManager secretsManager, 
        IRefreshTokenService refreshTokenService,
        ISettingsService settingsService)
    {
        _http = http;
        _secretsManager = secretsManager;
        _refreshTokenService = refreshTokenService;
        _settingsService = settingsService;
        
        _http.Timeout = TimeSpan.FromSeconds(15);
        
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

    public async Task Health(string? baseUrl = null)
    {
        var url = (baseUrl ?? _settingsService.Settings.ServerUrl).TrimEnd('/');
        var req = await _http.GetAsync($"{url}/health");
        req.EnsureSuccessStatusCode();
    }

    #region Helper Methods

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
                using var attemptReq = new HttpRequestMessage(method, GetAbsoluteUrl(url));
                var token2 = await _secretsManager.GetAccessTokenAsync();
                if (!string.IsNullOrWhiteSpace(token2))
                    attemptReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token2);

                if (body != null)
                    attemptReq.Content = Serialize(body);

                return await _http.SendAsync(attemptReq, ct2).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if ((int)res.StatusCode == 429)
            {
                res.Dispose();
                throw new RateLimitException();
            }

            if ((int)res.StatusCode >= 500)
            {
                res.Dispose();
                throw new ServerFailureException();
            }

            return res;
        }
        catch (Exception)
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

    public string GetAbsoluteUrl(string url) => GetAbsoluteUrl(_settingsService.Settings.ServerUrl, url);

    public static string GetAbsoluteUrl(string serverUrl, string url)
    {
        var trimmedServerUrl = serverUrl.TrimEnd('/');
        var baseAddress = $"{trimmedServerUrl}/api/v1/";
        var relative = url.TrimStart('/');
        return relative.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               ? relative
               : $"{baseAddress}{relative}";
    }

    #endregion
}