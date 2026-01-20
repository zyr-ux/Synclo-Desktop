using System;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.Services.SecretsManager;

namespace Synclo.Services;

public interface IRefreshTokenService
{
    Task<string> RefreshAsync();
    event Action<string>? TokenRefreshed;
    event Action? SessionExpired;
}

public sealed class RefreshTokenService(
    HttpClient http,
    ISecureStorage secureStorage,
    ICryptographyService cryptographyService) : IRefreshTokenService
{
    private const int SupportedKdfVersion = 1;

    private readonly ICryptographyService _cryptographyService =
        cryptographyService ?? throw new ArgumentNullException(nameof(cryptographyService));

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Task coalescing: all concurrent callers share the same refresh task
    private readonly object _refreshGate = new();
    private Task<string>? _ongoingRefresh;

    public event Action<string>? TokenRefreshed;
    public event Action? SessionExpired;

    public Task<string> RefreshAsync()
    {
        lock (_refreshGate)
        {
            _ongoingRefresh ??= RefreshInternalAsync();
            return _ongoingRefresh;
        }
    }

    private async Task<string> RefreshInternalAsync()
    {
        string? token = null;
        ExceptionDispatchInfo? capturedException = null;
        bool wasSessionExpired = false;

        try
        {
            token = await DoActualRefreshAsync();
        }
        catch (SessionExpiredException ex)
        {
            wasSessionExpired = true;
            capturedException = ExceptionDispatchInfo.Capture(ex);
        }
        catch (SecurityBreachException ex)
        {
            wasSessionExpired = true;
            capturedException = ExceptionDispatchInfo.Capture(ex);
        }
        catch (Exception ex)
        {
            capturedException = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            lock (_refreshGate)
            {
                _ongoingRefresh = null;
            }
        }

        // Events fire AFTER task is cleared to prevent deadlock from re-entrant calls
        if (capturedException != null)
        {
            if (wasSessionExpired)
            {
                SafeInvokeSessionExpired();
            }
            capturedException.Throw(); // Preserves original stack trace
        }

        SafeInvokeTokenRefreshed(token!);
        return token!;
    }

    private async Task<string> DoActualRefreshAsync()
    {
        var refreshToken =
            await secureStorage.LoadAsync(AccountService.RefreshToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            await ClearLocalSession().ConfigureAwait(false);
            throw new SessionExpiredException();
        }

        var body = new RefreshTokenRequest { refresh_token = refreshToken };

        try
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/refresh")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body, _jsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };

            using var res = await http.SendAsync(httpReq).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var errorContent = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (errorContent.Contains("reused", StringComparison.OrdinalIgnoreCase) ||
                        errorContent.Contains("family", StringComparison.OrdinalIgnoreCase))
                    {
                        await ClearLocalSession().ConfigureAwait(false);
                        throw new SecurityBreachException("Refresh token reuse detected.");
                    }
                }

                await ClearLocalSession().ConfigureAwait(false);
                throw new SessionExpiredException();
            }

            var content = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            var data = JsonSerializer.Deserialize<AuthResponse>(content, _jsonOptions);

            if (data == null ||
                string.IsNullOrWhiteSpace(data.access_token) ||
                string.IsNullOrWhiteSpace(data.refresh_token))
            {
                await ClearLocalSession().ConfigureAwait(false);
                throw new SessionExpiredException();
            }

            if (data.kdf_version.HasValue &&
                data.kdf_version.Value != SupportedKdfVersion)
            {
                await ClearLocalSession().ConfigureAwait(false);
                throw new SecurityException(
                    $"Account upgraded to security version {data.kdf_version}. Please update the app.");
            }

            await secureStorage
                .SaveAsync(AccountService.RefreshToken, data.refresh_token)
                .ConfigureAwait(false);

            await secureStorage
                .SaveAsync(AccountService.AccessToken, data.access_token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(data.salt))
                await secureStorage
                    .SaveAsync(_cryptographyService.Salt, data.salt)
                    .ConfigureAwait(false);

            if (data.kdf_version.HasValue)
                await secureStorage
                    .SaveAsync(
                        _cryptographyService.KdfVersion,
                        data.kdf_version.Value.ToString())
                    .ConfigureAwait(false);

            return data.access_token;
        }
        catch (HttpRequestException)
        {
            throw new NetworkFailureException();
        }
    }

    private void SafeInvokeTokenRefreshed(string token)
    {
        try
        {
            TokenRefreshed?.Invoke(token);
        }
        catch
        {
            // Suppress errors from event handlers
        }
    }

    private void SafeInvokeSessionExpired()
    {
        try
        {
            SessionExpired?.Invoke();
        }
        catch
        {
            // Suppress errors from event handlers
        }
    }

    private async Task ClearLocalSession()
    {
        await secureStorage.DeleteAsync(AccountService.AccessToken).ConfigureAwait(false);
        await secureStorage.DeleteAsync(AccountService.RefreshToken).ConfigureAwait(false);
        await secureStorage.DeleteAsync(AccountService.UserEmail).ConfigureAwait(false);
        await secureStorage.DeleteAsync(_cryptographyService.MasterKey).ConfigureAwait(false);
        await secureStorage.DeleteAsync(_cryptographyService.Salt).ConfigureAwait(false);
        await secureStorage.DeleteAsync(_cryptographyService.KdfVersion).ConfigureAwait(false);
    }
}