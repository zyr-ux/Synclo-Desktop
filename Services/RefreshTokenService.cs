using System;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.Services.SecretsManager;
using System.Collections.Generic;

namespace Synclo.Services;

public enum AuthRefreshResult
{
    Success,
    SessionExpired,
    NetworkFailure
}

public interface IRefreshTokenService
{
    // Services call this to trigger token refresh (fire-and-forget)
    void RaiseTokenExpired();
    
    // Services call this to wait for refresh result (with feedback)
    Task<AuthRefreshResult> WaitForRefreshAsync(CancellationToken ct = default);
    
    // RefreshTokenService fires these after processing
    event Action<string>? TokenRefreshed;
    event Action? SessionExpired;
}

public sealed class RefreshTokenService : IRefreshTokenService, IDisposable
{
    private const int SupportedKdfVersion = 1;

    private readonly HttpClient _http;
    private readonly ISecureStorage _secureStorage;
    private readonly ICryptographyService _cryptographyService;

    private readonly JsonSerializerOptions _jsonOptions;

    // Channel-based queue for refresh requests
    private readonly System.Threading.Channels.Channel<RefreshRequest> _refreshQueue;
    private readonly Task _processorTask;
    private readonly CancellationTokenSource _cts = new();
    
    // State-based suppression
    private bool _isRefreshing;
    private bool _refreshRequestedWhileRefreshing; // Remember intent if signal arrives during refresh
    private bool _sessionExpired; // Fast-fail for WaitForRefreshAsync
    private readonly object _refreshLock = new();
    
    // Waiters for feedback
    private readonly List<TaskCompletionSource<AuthRefreshResult>> _waiters = new();

    public event Action<string>? TokenRefreshed;
    public event Action? SessionExpired;

    public RefreshTokenService(
        HttpClient http,
        ISecureStorage secureStorage,
        ICryptographyService cryptographyService)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _cryptographyService = cryptographyService ?? throw new ArgumentNullException(nameof(cryptographyService));
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // Use bounded channel with capacity 1 and DropOldest strategy
        var options = new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
        };
        _refreshQueue = System.Threading.Channels.Channel.CreateBounded<RefreshRequest>(options);
        
        // Start background processor
        _processorTask = ProcessRefreshQueue();
    }

    public void RaiseTokenExpired()
    {
        lock (_refreshLock)
        {
            if (_isRefreshing)
            {
                // Remember intent - don't lose refresh request
                _refreshRequestedWhileRefreshing = true;
                return;
            }
        }
        
        // Directly write to channel (no event indirection)
        _refreshQueue.Writer.TryWrite(new RefreshRequest
        {
            RequestedAt = DateTime.UtcNow
        });
    }

    public Task<AuthRefreshResult> WaitForRefreshAsync(CancellationToken ct = default)
    {
        // Fast-fail if session already expired
        lock (_refreshLock)
        {
            if (_sessionExpired)
                return Task.FromResult(AuthRefreshResult.SessionExpired);
        }
        
        var tcs = new TaskCompletionSource<AuthRefreshResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        
        lock (_refreshLock)
        {
            _waiters.Add(tcs);
        }
        
        // Register cancellation and dispose registration after completion
        CancellationTokenRegistration registration = default;
        if (ct.CanBeCanceled)
        {
            registration = ct.Register(() =>
            {
                lock (_refreshLock)
                {
                    _waiters.Remove(tcs);
                }
                tcs.TrySetCanceled(ct);
            });
        }
        
        // Dispose registration when task completes
        if (ct.CanBeCanceled)
        {
            tcs.Task.ContinueWith(_ => registration.Dispose(), 
                TaskScheduler.Default);
        }
        
        return tcs.Task;
    }

    private async Task ProcessRefreshQueue()
    {
        try
        {
            await foreach (var request in _refreshQueue.Reader.ReadAllAsync(_cts.Token))
            {
                lock (_refreshLock)
                {
                    _isRefreshing = true;
                }
                
                AuthRefreshResult result = AuthRefreshResult.NetworkFailure; // Default to network failure
                string? token = null;
                
                try
                {
                    // Perform the actual refresh
                    token = await DoActualRefreshAsync();
                    result = AuthRefreshResult.Success;
                    
                    // Fire success event
                    SafeInvokeTokenRefreshed(token);
                }
                catch (SessionExpiredException)
                {
                    result = AuthRefreshResult.SessionExpired;
                    lock (_refreshLock)
                    {
                        _sessionExpired = true;
                    }
                    SafeInvokeSessionExpired();
                }
                catch (SecurityBreachException)
                {
                    result = AuthRefreshResult.SessionExpired;
                    lock (_refreshLock)
                    {
                        _sessionExpired = true;
                    }
                    SafeInvokeSessionExpired();
                }
                catch (NetworkFailureException)
                {
                    result = AuthRefreshResult.NetworkFailure;
                }
                catch
                {
                    // Unknown error - treat as network failure
                    result = AuthRefreshResult.NetworkFailure;
                }
                finally
                {
                    // Complete all waiters
                    CompleteAllWaiters(result);
                    
                    lock (_refreshLock)
                    {
                        _isRefreshing = false;
                        
                        // If refresh was requested while we were refreshing, trigger another one
                        if (_refreshRequestedWhileRefreshing)
                        {
                            _refreshRequestedWhileRefreshing = false;
                            _refreshQueue.Writer.TryWrite(new RefreshRequest
                            {
                                RequestedAt = DateTime.UtcNow
                            });
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void CompleteAllWaiters(AuthRefreshResult result)
    {
        List<TaskCompletionSource<AuthRefreshResult>> waitersToComplete;
        
        lock (_refreshLock)
        {
            waitersToComplete = new List<TaskCompletionSource<AuthRefreshResult>>(_waiters);
            _waiters.Clear();
        }
        
        foreach (var waiter in waitersToComplete)
        {
            waiter.TrySetResult(result);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _refreshQueue.Writer.Complete();
        try
        {
            _processorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore cleanup errors
        }
        _cts.Dispose();
    }

    private record RefreshRequest
    {
        public DateTime RequestedAt { get; init; }
    }

    private async Task<string> DoActualRefreshAsync()
    {
        var refreshToken =
            await _secureStorage.LoadAsync(AccountService.RefreshToken).ConfigureAwait(false);

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

            using var res = await _http.SendAsync(httpReq).ConfigureAwait(false);

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

            await _secureStorage
                .SaveAsync(AccountService.RefreshToken, data.refresh_token)
                .ConfigureAwait(false);

            await _secureStorage
                .SaveAsync(AccountService.AccessToken, data.access_token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(data.salt))
                await _secureStorage
                    .SaveAsync(_cryptographyService.Salt, data.salt)
                    .ConfigureAwait(false);

            if (data.kdf_version.HasValue)
                await _secureStorage
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
        await _secureStorage.DeleteAsync(AccountService.AccessToken).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(AccountService.RefreshToken).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(AccountService.UserEmail).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(_cryptographyService.MasterKey).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(_cryptographyService.Salt).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(_cryptographyService.KdfVersion).ConfigureAwait(false);
    }
}