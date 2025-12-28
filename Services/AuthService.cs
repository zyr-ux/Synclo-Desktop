using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

/// <summary>
/// Handles authentication operations including login, registration, and token management.
/// Implements E2EE authentication flow with password derivation via KDF.
/// </summary>
public sealed class AuthService(APIService api, HttpClient http)
{
    private const string Prefix = "com.synclo.app";
    public const string AccessToken = $"{Prefix}.auth.access_token";
    public const string RefreshToken = $"{Prefix}.auth.refresh_token";
    public const string UserEmail = $"{Prefix}.user.email";

    /// <summary>
    /// Retrieves KDF salt and version from server for a given email.
    /// Must be called before login/registration to obtain salt for password derivation.
    /// </summary>
    /// <param name="email">User email address</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>SaltResponse with salt and KDF version</returns>
    /// <exception cref="InvalidRequestException">Email not found or invalid format</exception>
    public async Task<SaltResponse> GetSaltAsyncInt(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty", nameof(email));

        try
        {
            using var res = await http.GetAsync($"/api/auth/salt?email={Uri.EscapeDataString(email)}", ct);
            var content = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidRequestException("User not found");

            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InvalidRequestException("Too many requests. Please wait before trying again.");

            if (!res.IsSuccessStatusCode)
                throw new ServerFailureException(content);

            var data = api.Deserialize<SaltResponse>(content);
            if (data == null)
                throw new ServerFailureException("Server returned success but salt was missing");

            return data;
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkFailureException();
        }
    }

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

        // Store E2EE credentials if present
        if (!string.IsNullOrWhiteSpace(data.encrypted_master_key))
            await SecureStorage.SaveAsync(SecureStorage.MasterKey, data.encrypted_master_key);

        if (!string.IsNullOrWhiteSpace(data.salt))
            await SecureStorage.SaveAsync(SecureStorage.Salt, data.salt);

        if (data.kdf_version.HasValue)
            await SecureStorage.SaveAsync(SecureStorage.KdfVersion, data.kdf_version.Value.ToString());

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

        // Store E2EE credentials if present
        if (!string.IsNullOrWhiteSpace(data.encrypted_master_key))
            await SecureStorage.SaveAsync(SecureStorage.MasterKey, data.encrypted_master_key);

        if (!string.IsNullOrWhiteSpace(data.salt))
            await SecureStorage.SaveAsync(SecureStorage.Salt, data.salt);

        if (data.kdf_version.HasValue)
            await SecureStorage.SaveAsync(SecureStorage.KdfVersion, data.kdf_version.Value.ToString());

        return data;
    }

    public async Task LogoutAsyncInt()
    {
        // Clear auth tokens
        await SecureStorage.DeleteAsync(AccessToken);
        await SecureStorage.DeleteAsync(RefreshToken);
        await SecureStorage.DeleteAsync(UserEmail);

        // Clear E2EE credentials for security
        await SecureStorage.DeleteAsync(SecureStorage.MasterKey);
        await SecureStorage.DeleteAsync(SecureStorage.Salt);
        await SecureStorage.DeleteAsync(SecureStorage.KdfVersion);
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
                // Check for security breach (token reuse)
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var content = await res.Content.ReadAsStringAsync(ct);
                    if (content.Contains("reused") || content.Contains("family"))
                    {
                        // Token reuse detected - security breach
                        await LogoutAsyncInt();
                        throw new SecurityBreachException("Refresh token reused. Session terminated for security.");
                    }
                }

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

            // Update tokens
            await UpdateTokensAsyncInt(data.access_token, data.refresh_token);

            // Update E2EE credentials if server re-wrapped them
            if (!string.IsNullOrWhiteSpace(data.encrypted_master_key))
                await SecureStorage.SaveAsync(SecureStorage.MasterKey, data.encrypted_master_key);

            if (!string.IsNullOrWhiteSpace(data.salt))
                await SecureStorage.SaveAsync(SecureStorage.Salt, data.salt);

            if (data.kdf_version.HasValue)
                await SecureStorage.SaveAsync(SecureStorage.KdfVersion, data.kdf_version.Value.ToString());

            return data.access_token;
        }
        catch (SessionExpiredException)
        {
            throw;
        }
        catch (SecurityBreachException)
        {
            throw;
        }
        catch (Exception)
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