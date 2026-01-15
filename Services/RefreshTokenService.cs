using System;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

public interface IRefreshTokenService
{
    Task<string> RefreshAsync(CancellationToken ct = default);
    event Action<string>? TokenRefreshed;
}

public sealed class RefreshTokenService(HttpClient http, 
    ISecureStorage secureStorage,
    CryptographyService cryptographyService) : IRefreshTokenService
{
    private const int SupportedKdfVersion = 1;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CryptographyService _cryptographyService = cryptographyService ?? throw new ArgumentNullException(nameof(cryptographyService));
    
    // Maintain local JSON options to avoid dependency on ApiService
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public event Action<string>? TokenRefreshed;

    public async Task<string> RefreshAsync(CancellationToken ct = default)
    {
        // 1. Capture the current token BEFORE locking to detect if it changes while we wait.
        var initialToken = await secureStorage.LoadAsync(AccountService.AccessToken);

        await _refreshLock.WaitAsync(ct);
        try
        {
            // 2. Check if the token has already been refreshed by another thread.
            var currentToken = await secureStorage.LoadAsync(AccountService.AccessToken);
            
            // If the token in storage is different from what we had before waiting, 
            // someone else refreshed it. Return the new token.
            if (currentToken != initialToken && !string.IsNullOrWhiteSpace(currentToken))
            {
                // Optionally notify here too if we want to be very chatty, but strictly not required
                // as the 'refresher' would have already notified.
                return currentToken;
            }
            
            // 3. Needs refresh. Proceed with logic.
            var refreshToken = await secureStorage.LoadAsync(AccountService.RefreshToken);
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
                
                using var res = await http.SendAsync(httpReq, ct);

                if (!res.IsSuccessStatusCode)
                {
                    if (res.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        var errorContent = await res.Content.ReadAsStringAsync(ct);
                        if (errorContent.Contains("reused") || errorContent.Contains("family"))
                        {
                            await ClearLocalSession();
                            throw new SecurityBreachException("Refresh token reuse detected.");
                        }
                    }

                    await ClearLocalSession();
                    throw new SessionExpiredException();
                }

                var content = await res.Content.ReadAsStringAsync(ct);
                var data = JsonSerializer.Deserialize<AuthResponse>(content, _jsonOptions);

                if (data == null ||
                    string.IsNullOrWhiteSpace(data.access_token) ||
                    string.IsNullOrWhiteSpace(data.refresh_token))
                {
                    await ClearLocalSession();
                    throw new SessionExpiredException();
                }

                if (data.kdf_version.HasValue && data.kdf_version.Value != SupportedKdfVersion)
                {
                    await ClearLocalSession();
                    throw new SecurityException(
                        $"Account upgraded to security version {data.kdf_version}. Please update the app.");
                }

                await secureStorage.SaveAsync(AccountService.AccessToken, data.access_token);
                await secureStorage.SaveAsync(AccountService.RefreshToken, data.refresh_token);

                if (!string.IsNullOrWhiteSpace(data.salt))
                    await secureStorage.SaveAsync(_cryptographyService.Salt, data.salt);

                if (data.kdf_version.HasValue)
                    await secureStorage.SaveAsync(
                        _cryptographyService.KdfVersion,
                        data.kdf_version.Value.ToString());

                // Notify listeners (e.g., WebSocketService) that a new token is available
                TokenRefreshed?.Invoke(data.access_token);

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

    private async Task ClearLocalSession()
    {
        await secureStorage.DeleteAsync(AccountService.AccessToken);
        await secureStorage.DeleteAsync(AccountService.RefreshToken);
        await secureStorage.DeleteAsync(AccountService.UserEmail);
        await secureStorage.DeleteAsync(_cryptographyService.MasterKey);
        await secureStorage.DeleteAsync(_cryptographyService.Salt);
        await secureStorage.DeleteAsync(_cryptographyService.KdfVersion);
    }
}
