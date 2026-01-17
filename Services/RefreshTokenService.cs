using System;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.Services.SecretsManager;

namespace Synclo.Services;

public interface IRefreshTokenService
{
    Task<string> RefreshAsync(CancellationToken ct = default);
    event Action<string>? TokenRefreshed;
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

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public event Action<string>? TokenRefreshed;

    public async Task<string> RefreshAsync(CancellationToken ct = default)
    {
        var initialToken =
            await secureStorage.LoadAsync(AccountService.AccessToken).ConfigureAwait(false);

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var currentToken =
                await secureStorage.LoadAsync(AccountService.AccessToken).ConfigureAwait(false);

            if (currentToken != initialToken && !string.IsNullOrWhiteSpace(currentToken))
            {
                SafeInvokeTokenRefreshed(currentToken);
                return currentToken;
            }

            var refreshToken =
                await secureStorage.LoadAsync(AccountService.RefreshToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new SessionExpiredException();

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

                using var res =
                    await http.SendAsync(httpReq, ct).ConfigureAwait(false);

                if (!res.IsSuccessStatusCode)
                {
                    if (res.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        var errorContent =
                            await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        if (errorContent.Contains("reused", StringComparison.OrdinalIgnoreCase) ||
                            errorContent.Contains("family", StringComparison.OrdinalIgnoreCase))
                        {
                            await ClearLocalSession().ConfigureAwait(false);
                            throw new SecurityBreachException(
                                "Refresh token reuse detected.");
                        }
                    }

                    await ClearLocalSession().ConfigureAwait(false);
                    throw new SessionExpiredException();
                }

                var content =
                    await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                var data =
                    JsonSerializer.Deserialize<AuthResponse>(content, _jsonOptions);

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

                SafeInvokeTokenRefreshed(data.access_token);

                return data.access_token;
            }
            catch (HttpRequestException)
            {
                throw new NetworkFailureException();
            }
        }
        finally
        {
            _refreshLock.Release();
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
            // Suppress errors
        }
    }

    // Lock removed here to prevent deadlock with RefreshAsync
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