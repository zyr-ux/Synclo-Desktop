using System;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

public interface IRefreshTokenService
{
    Task<string> RefreshAsync(CancellationToken ct = default);
}

public sealed class RefreshTokenService(APIService api, HttpClient http, ISecureStorage secureStorage) : IRefreshTokenService
{
    private const int SupportedKdfVersion = 1;

    public async Task<string> RefreshAsync(CancellationToken ct = default)
    {
        var refreshToken = await secureStorage.LoadAsync(AccountService.RefreshToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new SessionExpiredException();

        var body = new RefreshTokenRequest { refresh_token = refreshToken };

        try
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/refresh")
            {
                Content = api.Serialize(body)
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
            var data = api.Deserialize<AuthResponse>(content);

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
                await secureStorage.SaveAsync(CryptographyService.Salt, data.salt);

            if (data.kdf_version.HasValue)
                await secureStorage.SaveAsync(
                    CryptographyService.KdfVersion,
                    data.kdf_version.Value.ToString());

            return data.access_token;
        }
        catch (HttpRequestException)
        {
            throw new NetworkFailureException();
        }
    }

    private async Task ClearLocalSession()
    {
        await secureStorage.DeleteAsync(AccountService.AccessToken);
        await secureStorage.DeleteAsync(AccountService.RefreshToken);
        await secureStorage.DeleteAsync(AccountService.UserEmail);
        await secureStorage.DeleteAsync(CryptographyService.MasterKey);
        await secureStorage.DeleteAsync(CryptographyService.Salt);
        await secureStorage.DeleteAsync(CryptographyService.KdfVersion);
    }
}
