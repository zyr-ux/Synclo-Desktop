using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;
using Microsoft.Extensions.Logging;

namespace Synclo.Services.ClipboardService;

/// <summary>
/// Configuration settings for clipboard sync service
/// </summary>
public class ClipboardSyncConfig
{
    public int DebounceDelayMs { get; set; } = 500;
    public int RateLimitMaxSyncs { get; set; } = 5;
    public int RateLimitWindowMs { get; set; } = 10000;
    public int InactivityThresholdDays { get; set; } = 14;
    public int DefaultHistoryLimit { get; set; } = 100;
    public int DefaultSyncPageSize { get; set; } = 50;
    public int ShutdownTimeoutSeconds { get; set; } = 5;
    public int EchoSuppressionWindowMs { get; set; } = 2000;
    public int MaxTrackedHashes { get; set; } = 5;
    public int MinRefreshIntervalMs { get; set; } = 300;
    public int MaxRetryAttempts { get; set; } = 3;
    public int BaseRetryDelayMs { get; set; } = 1000;
    public int RetryProcessorTimeoutMs { get; set; } = 5000; // 5 seconds default timeout
}

/// <summary>
/// Main background service orchestrating clipboard synchronization.
/// Handles monitoring, debouncing, rate limiting, echo suppression, and bidirectional sync.
/// Updated to utilize high-performance batching and caching from ClipboardRepository.
/// </summary>
public class ClipboardSyncService(
    IClipboardMonitor monitor,
    IClipboardApiService clipboardApiService,
    IClipboardRepository repository,
    WebSocketService webSocketService,
    ISettingsService settingsService,
    NotificationService notificationService,
    CryptographyService cryptographyService,
    ISecureStorage secureStorage,
    ILogger<ClipboardSyncService> logger,
    ClipboardSyncConfig config
    ) : IDisposable
{
    private readonly IClipboardMonitor _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    private readonly IClipboardApiService _clipboardApiService = clipboardApiService ?? throw new ArgumentNullException(nameof(clipboardApiService));
    private readonly IClipboardRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly WebSocketService _webSocketService = webSocketService ?? throw new ArgumentNullException(nameof(webSocketService));
    private readonly ISettingsService _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    private readonly NotificationService _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
    private readonly CryptographyService _cryptographyService = cryptographyService ?? throw new ArgumentNullException(nameof(cryptographyService));
    private readonly ISecureStorage _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
    private readonly ILogger<ClipboardSyncService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ClipboardSyncConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    
    // Echo suppression - track multiple recent received hashes to prevent rapid echo loops
    private readonly Queue<(string Hash, DateTime Time)> _recentReceivedHashes = new();
    private readonly object _echoLock = new();
    
    // Debouncing
    private CancellationTokenSource? _debounceCts;
    
    // Rate limiting (sliding window: max 5 syncs per 10 seconds)
    private readonly Queue<DateTime> _syncTimestamps = new();
    private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    
    // Failed sync queue with retry logic - optimized with semaphore signaling
    private readonly Queue<(ClipboardDbModel Entry, int AttemptCount, DateTime NextRetryTime)> _failedSyncQueue = new();
    private readonly SemaphoreSlim _retryProcessorSemaphore = new(0, 1); // Semaphore for waking up retry processor
    private readonly SemaphoreSlim _retryLock = new(1, 1);
    
    // Fixed background task tracking with proper cleanup
    private readonly HashSet<Task> _backgroundTasks = new();
    private readonly object _taskLock = new();
    
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _disposed;
    private bool _isInitialized;
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        
        try
        {
            // Cold start check: wipe DB if inactive for too long
            if (_settingsService.Settings.last_sync.HasValue)
            {
                var inactivityDuration = DateTime.UtcNow - _settingsService.Settings.last_sync.Value;
                if (inactivityDuration.TotalDays > _config.InactivityThresholdDays)
                {
                    await _repository.ClearAllAsync();
                    _notificationService.ShowInfo(
                        $"For your security, clipboard history was cleared after {(int)inactivityDuration.TotalDays} days of inactivity. " +
                        "Refreshing from server...");
                    // Refresh history from server in background
                    RunBackgroundTask(async ct => await RefreshFromServerAsync(limit: _config.DefaultSyncPageSize), "Cold Start Refresh");
                }
            }

            // Subscribe to events
            _monitor.OnClipboardChanged += OnClipboardChanged;
            _webSocketService.OnMessageReceived += OnWebSocketMessageReceived;
            _repository.OnDataChanged += () => OnHistoryUpdated?.Invoke();

            // Retry syncing entries that failed before app closed (SyncedAt == null)
            var unsyncedEntries = await _repository.GetUnsyncedAsync();
            foreach (var entry in unsyncedEntries)
            {
                var content = entry.Content; // Capture for closure
                RunBackgroundTask(async ct => await ProcessOutgoingClipboardAsync(content), $"Retry Sync: {entry.Id}");
            }

            // Start monitoring if auto-sync is enabled
            if (_settingsService.Settings.auto_sync_enabled)
            {
                await _monitor.StartAsync();
            }

            // Start retry processor - now uses semaphore-based wake-up
            StartRetryProcessor();

            _isInitialized = true;
            _logger.LogInformation("ClipboardSyncService initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ClipboardSyncService");
            throw;
        }
    }

    private void StartRetryProcessor()
    {
        RunBackgroundTask(async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Wait for either:
                    // 1. A new failure is added to the queue (signaled via semaphore)
                    // 2. Timeout after 5 seconds (default interval)
                    var timeoutTask = Task.Delay(_config.RetryProcessorTimeoutMs, ct);
                    var semaphoreTask = _retryProcessorSemaphore.WaitAsync(ct);
                    
                    var completedTask = await Task.WhenAny(timeoutTask, semaphoreTask).ConfigureAwait(false);
                    
                    // If timeout occurred, continue to process
                    // If semaphore was signaled, process immediately
                    // Either way, we proceed to process the queue
                    
                    await ProcessRetryQueueAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in retry processor");
                    // Back off on error to prevent tight loops
                    await Task.Delay(10000, ct).ConfigureAwait(false);
                }
            }
        }, "Retry Processor");
    }

    private async Task ProcessRetryQueueAsync(CancellationToken ct)
    {
        await _retryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            var itemsToRetry = new List<(ClipboardDbModel Entry, int AttemptCount, DateTime NextRetryTime)>();
            
            // Process all items that are ready for retry
            while (_failedSyncQueue.Count > 0 && _failedSyncQueue.Peek().NextRetryTime <= now)
            {
                itemsToRetry.Add(_failedSyncQueue.Dequeue());
            }

            foreach (var (entry, attemptCount, _) in itemsToRetry)
            {
                try
                {
                    var serverId = await _clipboardApiService.SyncClipboardAsync(entry.Content);
                    
                    // FIX: Proper handover - delete client entry before inserting server entry
                    await _repository.DeleteByIdAsync(entry.Id);

                    // Create updated entry with server ID
                    var updatedEntry = new ClipboardDbModel
                    {
                        Id = serverId,
                        Content = entry.Content,
                        ContentHash = entry.ContentHash,
                        Ciphertext = entry.Ciphertext,
                        Nonce = entry.Nonce,
                        BlobVersion = entry.BlobVersion,
                        IsRemoteDeleted = false,
                        CreatedAt = entry.CreatedAt,
                        SyncedAt = DateTime.UtcNow
                    };
                    
                    await _repository.UpsertAsync(updatedEntry);
                    _settingsService.Settings.last_sync = DateTime.UtcNow;
                    _settingsService.Save();
                    
                    _logger.LogInformation($"Successfully synced clipboard entry {entry.Id} after {attemptCount} attempts");
                }
                catch (Exception ex)
                {
                    var newAttemptCount = attemptCount + 1;
                    if (newAttemptCount <= _config.MaxRetryAttempts)
                    {
                        // IMPROVEMENT: Use exponential backoff with config values
                        var delay = _config.BaseRetryDelayMs * (int)Math.Pow(2, newAttemptCount - 1);
                        var nextRetry = DateTime.UtcNow.AddMilliseconds(delay);
                        
                        await _retryLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            _failedSyncQueue.Enqueue((entry, newAttemptCount, nextRetry));
                            // Signal the retry processor immediately
                            _retryProcessorSemaphore.Release();
                        }
                        finally
                        {
                            _retryLock.Release();
                        }
                        
                        _logger.LogWarning(ex, $"Failed to sync clipboard entry {entry.Id} (attempt {newAttemptCount}/{_config.MaxRetryAttempts}). Next retry at {nextRetry}");
                    }
                    else
                    {
                        _logger.LogError(ex, $"Failed to sync clipboard entry {entry.Id} after {_config.MaxRetryAttempts} attempts. Giving up.");
                    }
                }
            }
        }
        finally
        {
            _retryLock.Release();
        }
    }

    public event Action? OnHistoryUpdated;

    /// <summary>
    /// Gets clipboard history for UI display.
    /// Uses the repository's cache for fast access.
    /// </summary>
    public async Task<List<ClipboardDbModel>> GetHistoryForUI(int limit = 100)
    {
        try
        {
            var entries = await _repository.GetAllAsync(limit).ConfigureAwait(false);
            return entries?.Where(e => !e.IsRemoteDeleted).ToList() ?? new List<ClipboardDbModel>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load history for UI");
            return new List<ClipboardDbModel>();
        }
    }

    /// <summary>
    /// Fetches data for the UI. Uses a "Cache-First, then Background Sync" strategy.
    /// This ensures the UI loads instantly while fresh data is fetched in parallel.
    /// Includes deduplication to prevent concurrent refreshes.
    /// </summary>
    public async Task<IReadOnlyList<ClipboardDbModel>> RefreshFromServerAsync(int limit = 100)
    {
        // Deduplication: Prevent concurrent refreshes
        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false))
        {
            // Another refresh is in progress, wait for it to complete
            await _refreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Return the result from the concurrent refresh
                return await _repository.GetAllAsync(limit).ConfigureAwait(false) ?? new List<ClipboardDbModel>();
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        try
        {
            // Fetch from database
            var localEntries = await _repository.GetAllAsync(limit).ConfigureAwait(false);
            
            // Trigger background sync to catch up with server
            _ = SyncInBackgroundAsync();
            
            // Return empty list if no data (don't return null)
            return localEntries ?? new List<ClipboardDbModel>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error loading clipboard history");
            _ = SyncInBackgroundAsync(); // Try to sync from server anyway
            return new List<ClipboardDbModel>();
        }
        finally
        {
            _refreshLock.Release();
        }
    }


    /// <summary>
    /// Downloads latest history from server and batch-inserts it into the DB.
    /// </summary>
    private async Task SyncInBackgroundAsync()
    {
        try
        {
            var masterKeyBase64 = await _secureStorage.LoadAsync(CryptographyService.MasterKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(masterKeyBase64))
            {
                _logger.LogInformation("Sync skipped: User not logged in");
                return; // Not logged in
            }

            var historyResponse = await _clipboardApiService.GetClipboardHistoryAsync(page: 1, pageSize: _config.DefaultSyncPageSize).ConfigureAwait(false);
            
            // Handle null or empty response gracefully
            if (historyResponse == null || historyResponse.history == null || historyResponse.history.Count == 0)
            {
                _logger.LogInformation("Sync completed: No history from server (empty account or new user)");
                _settingsService.Settings.last_sync = DateTime.UtcNow;
                _settingsService.Save();
                return;
            }

            var incomingBatch = new List<ClipboardDbModel>();
            foreach (var entry in historyResponse.history)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id) || string.IsNullOrEmpty(entry.plaintext))
                    continue;
                
                var contentHash = ComputeHash(entry.plaintext);
                var existingEntry = await _repository.GetByHashAsync(contentHash);
                
                if (existingEntry != null && existingEntry.SyncedAt.HasValue && existingEntry.Id == entry.id)
                    continue;
                
                var createdAt = existingEntry?.CreatedAt ?? entry.timestamp;
                
                var dbEntry = new ClipboardDbModel
                {
                    Id = entry.id,
                    Content = entry.plaintext,
                    ContentHash = contentHash,
                    Ciphertext = entry.ciphertext ?? string.Empty,
                    Nonce = entry.nonce ?? string.Empty,
                    BlobVersion = entry.blob_version,
                    IsRemoteDeleted = false,
                    CreatedAt = createdAt,
                    SyncedAt = DateTime.UtcNow
                };
                
                incomingBatch.Add(dbEntry);
                
                if (existingEntry != null && existingEntry.Id != entry.id)
                {
                    await _repository.DeleteByIdAsync(existingEntry.Id);
                }
            }

            // Use optimized batch upsert (fires single event)
            if (incomingBatch.Count > 0)
            {
                await _repository.UpsertAsync(incomingBatch).ConfigureAwait(false);
                _logger.LogInformation($"Sync completed: {incomingBatch.Count} entries synced from server");
            }
            else
            {
                _logger.LogInformation("Sync completed: No valid entries to sync");
            }

            _settingsService.Settings.last_sync = DateTime.UtcNow;
            _settingsService.Save();
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            // Network error - don't spam user with notifications
            _logger.LogWarning(ex, $"Sync failed: Network error");
        }
        catch (Exception ex)
        {
            // Unexpected error - log it
            _logger.LogError(ex, $"Sync failed: {ex.GetType().Name}");
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

    /// <summary>
    /// Deletes a clipboard entry from both the server and local database.
    /// Server deletion happens first to ensure consistency.
    /// </summary>
    /// <param name="clipboardId">The ID of the clipboard entry to delete</param>
    public async Task DeleteClipboardEntryAsync(string clipboardId)
    {
        if (string.IsNullOrEmpty(clipboardId))
            throw new ArgumentException("Clipboard ID cannot be null or empty", nameof(clipboardId));
        
        try
        {
            // Step 1: Delete from server first
            var response = await _clipboardApiService.DeleteClipboardAsync(clipboardId);
            
            // Step 2: Delete from local database after successful server deletion
            await _repository.DeleteByIdAsync(clipboardId);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"Failed to delete clipboard entry: {ex.Message}");
            _logger.LogError(ex, $"Failed to delete clipboard entry {clipboardId}");
            throw;
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
        RunBackgroundTask(async ct =>
        {
            try
            {
                await Task.Delay(_config.DebounceDelayMs, cts.Token).ConfigureAwait(false);
                await ProcessOutgoingClipboardAsync(content).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled, ignore
            }
        }, "Clipboard Changed");
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
            // Duplicate check: skip ONLY if it matches the very last entry (consecutive debounce)
            // This mimics standard OS behavior: A -> B -> A is allowed, but A -> A is debounced.
            var lastEntries = await _repository.GetAllAsync(1);
            var lastEntry = lastEntries.FirstOrDefault();

            if (lastEntry != null && lastEntry.ContentHash == contentHash)
            {
                // Consecutive duplicate detected, debounce it
                return;
            }
            
            string clientId;
            
            // Generate deterministic client-side ID using hash + timestamp
            clientId = GenerateClientId(contentHash);
            
            // Save to SQLite immediately (optimistic write)
            var dbEntry = new ClipboardDbModel
            {
                Id = clientId, // Use deterministic client ID
                Content = content,
                ContentHash = contentHash,
                Ciphertext = string.Empty, // Will be filled after encryption
                Nonce = string.Empty,
                BlobVersion = _settingsService.Settings.blob_version,
                IsRemoteDeleted = false,
                CreatedAt = DateTime.UtcNow,
                SyncedAt = null // Mark as unsynced
            };
            
            await _repository.UpsertAsync(dbEntry);
            
            // Track the hash to prevent echo detection when we receive it back from server
            TrackReceivedHash(contentHash);
            
            // Sync to server in background with proper error handling
            RunBackgroundTask(async ct =>
            {
                try
                {
                    var serverId = await _clipboardApiService.SyncClipboardAsync(content);
                    
                    // Get the current entry (might have been updated)
                    var currentEntry = await _repository.GetByIdAsync(clientId);
                    if (currentEntry == null)
                    {
                        _logger.LogWarning($"Entry {clientId} not found after sync attempt");
                        return;
                    }
                    
                    // FIX: Proper handover - delete client entry first
                    await _repository.DeleteByIdAsync(clientId);
                    
                    // Create updated entry with server ID
                    var syncedDbEntry = new ClipboardDbModel
                    {
                        Id = serverId, // Update to server ID
                        Content = currentEntry.Content,
                        ContentHash = currentEntry.ContentHash,
                        Ciphertext = currentEntry.Ciphertext,
                        Nonce = currentEntry.Nonce,
                        BlobVersion = currentEntry.BlobVersion,
                        IsRemoteDeleted = false,
                        CreatedAt = currentEntry.CreatedAt,
                        SyncedAt = DateTime.UtcNow
                    };
                    
                    await _repository.UpsertAsync(syncedDbEntry);
                    _settingsService.Settings.last_sync = DateTime.UtcNow;
                    _settingsService.Save();
                    
                    _logger.LogInformation($"Successfully synced clipboard entry {clientId} -> {serverId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to sync clipboard entry {clientId}");
                    _notificationService.ShowError($"Failed to sync clipboard: {ex.Message}");
                    
                    // Add to retry queue with exponential backoff
                    var entry = await _repository.GetByIdAsync(clientId);
                    if (entry != null)
                    {
                        await _retryLock.WaitAsync(ct);
                        try
                        {
                            var nextRetry = DateTime.UtcNow.AddMilliseconds(_config.BaseRetryDelayMs);
                            _failedSyncQueue.Enqueue((entry, 1, nextRetry));
                            // Signal the retry processor immediately instead of waiting for timeout
                            _retryProcessorSemaphore.Release();
                        }
                        finally
                        {
                            _retryLock.Release();
                        }
                    }
                }
            }, $"Sync to Server: {clientId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Clipboard processing error");
            _notificationService.ShowError($"Clipboard processing error: {ex.Message}");
        }
    }

    private string GenerateClientId(string contentHash)
    {
        // Generate deterministic client ID using hash + timestamp (increased to 16 chars for safety)
        var timestamp = DateTime.UtcNow.Ticks.ToString("x");
        return $"client_{contentHash.Substring(0, 16)}_{timestamp}";
    }

    private void OnWebSocketMessageReceived(string message)
    {
        // Process incoming clipboard updates in background
        RunBackgroundTask(async ct => await ProcessIncomingClipboardAsync(message), "WebSocket Message");
    }

    private async Task ProcessIncomingClipboardAsync(string message)
    {
        try
        {
            // Try to deserialize as clipboard entry
            ClipboardEntry? entry;
            try
            {
                entry = _clipboardApiService.Deserialize<ClipboardEntry>(message);
            }
            catch (Exception ex)
            {
                // Not a valid clipboard entry (could be heartbeat, error, etc.)
                _logger.LogDebug($"Message not a clipboard entry: {message}. Error: {ex.Message}");
                return;
            }
            
            // Validate required fields - if missing, this is not a clipboard message
            if (entry == null ||
                string.IsNullOrEmpty(entry.id) ||
                string.IsNullOrEmpty(entry.ciphertext) ||
                string.IsNullOrEmpty(entry.nonce))
            {
                _logger.LogDebug("Invalid clipboard entry format - missing required fields");
                return;
            }
            
            // Decrypt content
            var masterKeyBase64 = await _secureStorage.LoadAsync(CryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
            {
                _logger.LogWarning("Master key not found - cannot decrypt clipboard content");
                return;
            }
            
            var masterKey = CryptographyService.FromBase64Static(masterKeyBase64);
            var ciphertext = CryptographyService.FromBase64Static(entry.ciphertext ?? string.Empty);
            var nonce = CryptographyService.FromBase64Static(entry.nonce ?? string.Empty);
            
            var plaintext = _cryptographyService.DecryptClipboard(ciphertext, nonce, masterKey);
            var contentHash = ComputeHash(plaintext);
            
            // Update echo suppression tracking
            TrackReceivedHash(contentHash);
            
            // FIX: Deduplication - check if we already have this content
            var existingEntry = await _repository.GetByHashAsync(contentHash);
            if (existingEntry != null)
            {
                // If we have a client entry, delete it first before adding the server entry
                if (existingEntry.Id.StartsWith("client_"))
                {
                    await _repository.DeleteByIdAsync(existingEntry.Id);
                }
                // If we already have a server entry with the same content, skip this update
                else if (existingEntry.Id == entry.id)
                {
                    _logger.LogDebug($"Skipping duplicate clipboard entry: {entry.id}");
                    return;
                }
            }
            
            // Save to SQLite
            var dbEntry = new ClipboardDbModel
            {
                Id = entry.id,
                Content = plaintext,
                ContentHash = contentHash,
                Ciphertext = entry.ciphertext ?? string.Empty,
                Nonce = entry.nonce ?? string.Empty,
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
            _logger.LogError(ex, $"Failed to process WebSocket message");
        }
    }

    // IMPROVEMENT: Time-based echo cleanup
    private void TrackReceivedHash(string contentHash)
    {
        lock (_echoLock)
        {
            var now = DateTime.UtcNow;
            
            // Remove old entries outside the suppression window (time-based cleanup)
            while (_recentReceivedHashes.Count > 0)
            {
                var (hash, time) = _recentReceivedHashes.Peek();
                if ((now - time).TotalMilliseconds > _config.EchoSuppressionWindowMs)
                {
                    _recentReceivedHashes.Dequeue();
                }
                else
                {
                    break; // Queue is ordered by time, so we can stop
                }
            }
            
            // Add new hash
            _recentReceivedHashes.Enqueue((contentHash, now));
            
            // Also limit by count as a safety measure
            while (_recentReceivedHashes.Count > _config.MaxTrackedHashes)
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
                if (hash == contentHash && (now - time).TotalMilliseconds < _config.EchoSuppressionWindowMs)
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
            var windowStart = now.AddMilliseconds(-_config.RateLimitWindowMs);
            
            // Remove timestamps outside the window
            while (_syncTimestamps.Count > 0 && _syncTimestamps.Peek() < windowStart)
            {
                _syncTimestamps.Dequeue();
            }
            
            // Check if limit exceeded
            if (_syncTimestamps.Count >= _config.RateLimitMaxSyncs)
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

    /// <summary>
    /// Runs a task in the background with proper exception handling and tracking.
    /// Use this instead of fire-and-forget (_ = Task) to prevent silent failures.
    /// Fixed to properly clean up completed tasks to prevent memory leaks.
    /// </summary>
    private void RunBackgroundTask(Func<CancellationToken, Task> taskFunc, string taskName = "Background Task")
    {
        var cts = _shutdownCts.Token;
        
        // Create the task with proper exception handling
        var task = Task.Run(async () =>
        {
            try
            {
                await taskFunc(cts).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown, ignore
                _logger.LogInformation($"{taskName} was cancelled");
            }
            catch (Exception ex)
            {
                // Log unexpected errors - these would have been silent before
                _logger.LogError(ex, $"{taskName} failed: {ex.GetType().Name}");
            }
        }, cts);

        // Add the task to our tracking collection
        lock (_taskLock)
        {
            _backgroundTasks.Add(task);
        }
        
        // Set up continuation to remove the task when it completes
        task.ContinueWith(t =>
        {
            lock (_taskLock)
            {
                _backgroundTasks.Remove(t);
            }
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    public async Task ShutdownAsync()
    {
        try
        {
            // Signal all background tasks to cancel
            _shutdownCts.Cancel();
            
            // Get a snapshot of current tasks to wait for
            Task[] tasksToWait;
            lock (_taskLock)
            {
                tasksToWait = _backgroundTasks.ToArray();
            }
            
            // Wait for all background tasks to complete (with timeout)
            if (tasksToWait.Length > 0)
            {
                try
                {
                    await Task.WhenAll(tasksToWait).WaitAsync(TimeSpan.FromSeconds(_config.ShutdownTimeoutSeconds)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning($"Shutdown timeout: {tasksToWait.Length} background tasks did not complete in time");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error during shutdown");
                }
            }
            
            // Signal retry processor to wake up and exit
            _retryProcessorSemaphore.Release();
            
            // Cleanup deleted entries on graceful shutdown
            await _repository.DeleteAllMarkedAsync().ConfigureAwait(false);
            await _monitor.StopAsync().ConfigureAwait(false);
            
            _logger.LogInformation("ClipboardSyncService shutdown completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during shutdown");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        try
        {
            _monitor.OnClipboardChanged -= OnClipboardChanged;
            _webSocketService.OnMessageReceived -= OnWebSocketMessageReceived;
            
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            
            _rateLimitLock.Dispose();
            _refreshLock.Dispose();
            _retryLock.Dispose();
            _retryProcessorSemaphore.Dispose();
            
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
            
            _logger.LogInformation("ClipboardSyncService disposed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disposal");
        }
    }
}