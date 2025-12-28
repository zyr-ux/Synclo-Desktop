using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

public sealed class AuthService(APIService api, HttpClient http)
{
    private const string Prefix = "com.synclo.app";
    public const string AccessToken = $"{Prefix}.auth.access_token";
    public const string RefreshToken = $"{Prefix}.auth.refresh_token";
    public const string UserEmail = $"{Prefix}.user.email";

    public async Task<AuthResponse> LoginAsyncInt(LoginRequest request, CancellationToken ct = default)
    {
        using var res = await api.PostAsync("/api/login", request, ct);
        var content = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            throw new InvalidCredentialsException(content);

        if (!res.IsSuccessStatusCode)
            throw new ServerFailureException(content);

        var data = api.Deserialize<AuthResponse>(content);

        if (string.IsNullOrWhiteSpace(data?.access_token) || string.IsNullOrWhiteSpace(data?.refresh_token))
            throw new ServerFailureException("Server returned success but tokens were empty.");

        // Securely store credentials and PII
        await SecureStorage.SaveAsync(AccessToken, data.access_token);
        await SecureStorage.SaveAsync(RefreshToken, data.refresh_token);
        await SecureStorage.SaveAsync(UserEmail, request.email);
        return data;
    }

    public async Task<AuthResponse> RegisterAsyncInt(RegisterRequest request, CancellationToken ct = default)
    {
        using var res = await api.PostAsync("/api/register", request, ct);
        var content = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            // 409 = User exists, 400 = Validation error, 500 = Server fire
            throw res.StatusCode switch
            {
                HttpStatusCode.Conflict => new UserAlreadyExistsException(content),
                HttpStatusCode.BadRequest => new InvalidRequestException(content),
                _ => new ServerFailureException(content)
            };

        var data = api.Deserialize<AuthResponse>(content);

        if (string.IsNullOrWhiteSpace(data?.access_token) || string.IsNullOrWhiteSpace(data?.refresh_token))
            throw new ServerFailureException("Missing tokens in response.");

        // Securely store credentials and PII
        await SecureStorage.SaveAsync(AccessToken, data.access_token);
        await SecureStorage.SaveAsync(RefreshToken, data.refresh_token);
        await SecureStorage.SaveAsync(UserEmail, request.email);
        return data;
    }

    public async Task LogoutAsyncInt()
    {
        await SecureStorage.DeleteAsync(AccessToken);
        await SecureStorage.DeleteAsync(RefreshToken);
        await SecureStorage.DeleteAsync(UserEmail);
    }

    public async Task<string> RefreshTokenAsyncInt(CancellationToken ct)
    {
        var refreshToken = await SecureStorage.LoadAsync(RefreshToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new SessionExpiredException();

        var body = new { refresh_token = refreshToken };

        try
        {
            // Use the shared serialize/deserialize logic from the API service
            using var res = await http.PostAsync("/api/refresh", api.Serialize(body), ct);

            if (!res.IsSuccessStatusCode)
            {
                await LogoutAsyncInt();
                throw new SessionExpiredException();
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            var data = api.Deserialize<AuthResponse>(json);

            if (data == null || string.IsNullOrWhiteSpace(data.access_token) ||
                string.IsNullOrWhiteSpace(data.refresh_token))
            {
                await LogoutAsyncInt();
                throw new SessionExpiredException();
            }

            // Update storage
            await UpdateTokensAsyncInt(data.access_token, data.refresh_token);

            return data.access_token;
        }
        catch (Exception ex) when (ex is not SessionExpiredException)
        {
            throw new NetworkFailureException();
        }
    }

    public async Task UpdateTokensAsyncInt(string accessToken, string refreshToken)
    {
        await SecureStorage.SaveAsync(AccessToken, accessToken);
        await SecureStorage.SaveAsync(RefreshToken, refreshToken);
    }
}