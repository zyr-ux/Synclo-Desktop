using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services.ClipboardService;

/// <summary>
/// Main background service orchestrating clipboard synchronization.
/// Handles monitoring, debouncing, rate limiting, echo suppression, and bidirectional sync.
/// </summary>
public class ClipboardSyncService : IDisposable
{
    private readonly IClipboardMonitor _monitor;
    private readonly ClipboardApiService _clipboardApiService;
    private readonly IClipboardRepository _repository;
    private readonly WebSocketService _webSocketService;
    private readonly ISettingsService _settingsService;
    private readonly NotificationService _notificationService;
    private readonly CryptographyService _cryptographyService;
    private readonly ISecureStorage _secureStorage;
    private readonly APIService _apiService;
    private readonly AccountService _accountService;

    // Echo suppression - track multiple recent received hashes to prevent rapid echo loops
    private readonly Queue<(string Hash, DateTime Time)> _recentReceivedHashes = new();
    private readonly object _echoLock = new();
    private const int EchoSuppressionWindowMs = 2000;
    private const int MaxTrackedHashes = 5;
    
    // Cold start configuration
    private const int InactivityThresholdDays = 14;

    // Debouncing
    private CancellationTokenSource? _debounceCts;
    private const int DebounceDelayMs = 500;

    // Rate limiting (sliding window: max 5 syncs per 10 seconds)
    private readonly Queue<DateTime> _syncTimestamps = new();
    private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private const int RateLimitMaxSyncs = 5;
    private const int RateLimitWindowMs = 10000;

    private bool _disposed;
    private bool _isInitialized;

    public ClipboardSyncService(
        IClipboardMonitor monitor,
        ClipboardApiService clipboardApiService,
        IClipboardRepository repository,
        WebSocketService webSocketService,
        ISettingsService settingsService,
        NotificationService notificationService,
        CryptographyService cryptographyService,
        ISecureStorage secureStorage,
        APIService apiService,
        AccountService accountService)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _clipboardApiService = clipboardApiService ?? throw new ArgumentNullException(nameof(clipboardApiService));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _webSocketService = webSocketService ?? throw new ArgumentNullException(nameof(webSocketService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _cryptographyService = cryptographyService ?? throw new ArgumentNullException(nameof(cryptographyService));
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        
        // Cold start check: wipe DB if inactive for too long
        if (_settingsService.Settings.last_sync.HasValue)
        {
            var inactivityDuration = DateTime.UtcNow - _settingsService.Settings.last_sync.Value;
            if (inactivityDuration.TotalDays > InactivityThresholdDays)
            {
                await _repository.ClearAllAsync();
                _notificationService.ShowInfo(
                    $"For your security, clipboard history was cleared after {(int)inactivityDuration.TotalDays} days of inactivity. " +
                    "Refreshing from server...");
                
                // Refresh history from server in background
                _ = RefreshFromServerAsync();
            }
        }

        // Subscribe to logout for cleanup
        _accountService.OnLogout += OnLogoutAsync;
        
        // Subscribe to login for re-initialization
        _accountService.OnLogin += OnLoginAsync;

        // Subscribe to clipboard monitor events
        _monitor.OnClipboardChanged += OnClipboardChanged;

        // Subscribe to WebSocket messages
        _webSocketService.OnMessageReceived += OnWebSocketMessageReceived;
        
        // Retry syncing entries that failed before app closed (SyncedAt == null)
        var unsyncedEntries = await _repository.GetUnsyncedAsync();
        foreach (var entry in unsyncedEntries)
        {
            _ = ProcessOutgoingClipboardAsync(entry.Content);
        }

        // Start monitoring if auto-sync is enabled
        if (_settingsService.Settings.auto_sync_enabled)
        {
            await _monitor.StartAsync();
        }

        _isInitialized = true;
    }
    
    private async Task OnLogoutAsync()
    {
        // Stop monitor first to prevent clipboard events on a cleared DB
        await _monitor.StopAsync();
        await _repository.ClearAllAsync();
    }
    
    private async Task OnLoginAsync()
    {
        // Refresh clipboard history from server after login
        await RefreshFromServerAsync();
        
        // Restart monitor if auto-sync is enabled
        if (_settingsService.Settings.auto_sync_enabled && !_monitor.IsRunning)
        {
            await _monitor.StartAsync();
        }
    }

    private async Task RefreshFromServerAsync()
    {
        try
        {
            var masterKeyBase64 = await _secureStorage.LoadAsync(CryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
                return; // Not logged in

            var historyResponse = await _clipboardApiService.GetClipboardHistoryAsync(page: 1, pageSize: 50);
            
            foreach (var entry in historyResponse.history)
            {
                if (string.IsNullOrEmpty(entry.plaintext))
                    continue;
                    
                var dbEntry = new ClipboardDbModel
                {
                    Id = entry.id,
                    Content = entry.plaintext,
                    ContentHash = ComputeHash(entry.plaintext),
                    Ciphertext = entry.ciphertext,
                    Nonce = entry.nonce,
                    BlobVersion = entry.blob_version,
                    IsRemoteDeleted = false,
                    CreatedAt = entry.timestamp,
                    SyncedAt = DateTime.UtcNow
                };
                
                await _repository.UpsertAsync(dbEntry);
            }
            
            _settingsService.Settings.last_sync = DateTime.UtcNow;
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to refresh from server: {ex.Message}");
        }
    }


    public async Task SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            await _monitor.StartAsync();
        }
        else
        {
            await _monitor.StopAsync();
        }
    }

    private void OnClipboardChanged(string content)
    {
        // Cancel and dispose any pending debounce
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();

        var cts = _debounceCts;

        // Debounce: wait for clipboard stability
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelayMs, cts.Token);
                await ProcessOutgoingClipboardAsync(content);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled, ignore
            }
        });
    }

    private async Task ProcessOutgoingClipboardAsync(string content)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
                return;

            // Check if auto-sync is enabled
            if (!_settingsService.Settings.auto_sync_enabled)
                return;

            // Compute content hash
            var contentHash = ComputeHash(content);

            // Echo suppression: ignore if this matches recently received content
            if (IsEchoSuppressed(contentHash))
                return;

            // Rate limiting
            if (!await CheckRateLimitAsync())
            {
                _notificationService.ShowWarning("Clipboard sync rate limit exceeded. Please slow down.");
                return;
            }

            // Duplicate check: skip if identical content was recently synced
            var existingEntry = await _repository.GetByHashAsync(contentHash);
            if (existingEntry != null && existingEntry.SyncedAt.HasValue)
            {
                // Content already exists and was synced, skip duplicate
                return;
            }

            string tempId;

            if (existingEntry != null)
            {
                // Optimization: Reuse existing unsynced entry to prevent duplicates
                // Update CreatedAt to bump it to the top of the list locally
                tempId = existingEntry.Id;
                existingEntry.CreatedAt = DateTime.UtcNow;
                await _repository.UpsertAsync(existingEntry);
            }
            else
            {
                // Save to SQLite immediately (optimistic write)
                tempId = Guid.NewGuid().ToString();
                var dbEntry = new ClipboardDbModel
                {
                    Id = tempId, // Temporary ID until server responds
                    Content = content,
                    ContentHash = contentHash,
                    Ciphertext = string.Empty, // Will be filled after encryption
                    Nonce = string.Empty,
                    BlobVersion = _settingsService.Settings.blob_version,
                    IsRemoteDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                    SyncedAt = null
                };
                await _repository.UpsertAsync(dbEntry);
            }

            // Encrypt and sync to server in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var serverId = await _clipboardApiService.SyncClipboardAsync(content);

                    var tempEntry = await _repository.GetByIdAsync(tempId);
                    
                    await _repository.DeleteByIdAsync(tempId);
                    
                    var syncedDbEntry = new ClipboardDbModel
                    {
                        Id = serverId,
                        Content = content,
                        ContentHash = contentHash,
                        Ciphertext = tempEntry?.Ciphertext ?? string.Empty,
                        Nonce = tempEntry?.Nonce ?? string.Empty,
                        BlobVersion = _settingsService.Settings.blob_version,
                        IsRemoteDeleted = false,
                        CreatedAt = DateTime.UtcNow,
                        SyncedAt = DateTime.UtcNow
                    };
                    
                    await _repository.UpsertAsync(syncedDbEntry);

                    _settingsService.Settings.last_sync = DateTime.UtcNow;
                    _settingsService.Save();
                }
                catch (Exception ex)
                {
                    _notificationService.ShowError($"Failed to sync clipboard: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"Clipboard processing error: {ex.Message}");
        }
    }

    private void OnWebSocketMessageReceived(string message)
    {
        // Process incoming clipboard updates in background
        _ = Task.Run(async () => await ProcessIncomingClipboardAsync(message));
    }

    private async Task ProcessIncomingClipboardAsync(string message)
    {
        try
        {
            // Try to deserialize as clipboard entry
            ClipboardEntry? entry;
            try
            {
                entry = _apiService.Deserialize<ClipboardEntry>(message);
            }
            catch
            {
                // Not a valid clipboard entry (could be heartbeat, error, etc.)
                return;
            }
            
            // Validate required fields - if missing, this is not a clipboard message
            if (entry == null || 
                string.IsNullOrEmpty(entry.id) || 
                string.IsNullOrEmpty(entry.ciphertext) || 
                string.IsNullOrEmpty(entry.nonce))
            {
                return;
            }

            // Decrypt content
            var masterKeyBase64 = await _secureStorage.LoadAsync(CryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
                return;

            var masterKey = CryptographyService.FromBase64Static(masterKeyBase64);
            var ciphertext = CryptographyService.FromBase64Static(entry.ciphertext);
            var nonce = CryptographyService.FromBase64Static(entry.nonce);

            var plaintext = _cryptographyService.DecryptClipboard(ciphertext, nonce, masterKey);
            var contentHash = ComputeHash(plaintext);

            // Update echo suppression tracking
            AddReceivedHash(contentHash);

            // Save to SQLite
            var dbEntry = new ClipboardDbModel
            {
                Id = entry.id,
                Content = plaintext,
                ContentHash = contentHash,
                Ciphertext = entry.ciphertext,
                Nonce = entry.nonce,
                BlobVersion = entry.blob_version,
                IsRemoteDeleted = false,
                CreatedAt = entry.timestamp,
                SyncedAt = DateTime.UtcNow
            };

            await _repository.UpsertAsync(dbEntry);

            // Write to OS clipboard via cross-platform monitor abstraction
            await _monitor.SetClipboardTextAsync(plaintext);
        }
        catch (Exception ex)
        {
            // Log error but don't notify user for every failed message (could be spam)
            System.Diagnostics.Debug.WriteLine($"Failed to process WebSocket message: {ex.Message}");
        }
    }

    private void AddReceivedHash(string contentHash)
    {
        lock (_echoLock)
        {
            _recentReceivedHashes.Enqueue((contentHash, DateTime.UtcNow));
            
            // Keep only the most recent hashes
            while (_recentReceivedHashes.Count > MaxTrackedHashes)
            {
                _recentReceivedHashes.Dequeue();
            }
        }
    }

    private bool IsEchoSuppressed(string contentHash)
    {
        lock (_echoLock)
        {
            var now = DateTime.UtcNow;
            
            // Check if this hash matches any recently received content within the suppression window
            foreach (var (hash, time) in _recentReceivedHashes)
            {
                if (hash == contentHash && (now - time).TotalMilliseconds < EchoSuppressionWindowMs)
                {
                    return true;
                }
            }
            
            return false;
        }
    }

    private async Task<bool> CheckRateLimitAsync()
    {
        await _rateLimitLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var windowStart = now.AddMilliseconds(-RateLimitWindowMs);

            // Remove timestamps outside the window
            while (_syncTimestamps.Count > 0 && _syncTimestamps.Peek() < windowStart)
            {
                _syncTimestamps.Dequeue();
            }

            // Check if limit exceeded
            if (_syncTimestamps.Count >= RateLimitMaxSyncs)
            {
                return false;
            }

            // Add current timestamp
            _syncTimestamps.Enqueue(now);
            return true;
        }
        finally
        {
            _rateLimitLock.Release();
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    public async Task ShutdownAsync()
    {
        // Cleanup deleted entries on graceful shutdown
        await _repository.DeleteAllMarkedAsync();
        await _monitor.StopAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitor.OnClipboardChanged -= OnClipboardChanged;
        _webSocketService.OnMessageReceived -= OnWebSocketMessageReceived;
        _accountService.OnLogout -= OnLogoutAsync;
        _accountService.OnLogin -= OnLoginAsync;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _rateLimitLock.Dispose();
    }
}
