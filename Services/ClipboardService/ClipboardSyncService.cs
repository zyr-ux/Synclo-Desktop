using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Synclo.Models;
using Synclo.Services.API;
using Synclo.Services.ClipboardMonitor;
using Synclo.Services.SecretsManager;
using Synclo.Services.Utilities;

namespace Synclo.Services.ClipboardService;

// Unified pipeline event - supports both local content and remote entries
internal abstract record ClipboardPipelineEvent
{
    public record LocalUpsert(string Content, DateTime Timestamp) : ClipboardPipelineEvent;

    public record RemoteEntry(ClipboardEntry Entry, DateTime Timestamp) : ClipboardPipelineEvent;
}

public interface IClipboardSyncService : IDisposable
{
    Task InitializeAsync();
    Task<List<ClipboardDbModel>> GetHistoryForUI(int limit = 100);
    Task<IReadOnlyList<ClipboardDbModel>> RefreshFromServerAsync(int limit = 100);
    Task DeleteClipboardEntryAsync(string clipboardId);
    Task ShutdownAsync();
    event Action? OnHistoryUpdated;
}

public class ClipboardSyncService(
    IClipboardMonitor monitor,
    IClipboardApiService clipboardApiService,
    IClipboardRepository repository,
    IWebSocketService webSocketService,
    ISettingsService settingsService,
    INotificationService notificationService,
    IAccountService accountService,
    ICryptographyService cryptographyService,
    ISecureStorage secureStorage,
    IUtils utils,
    ILogger<ClipboardSyncService> logger
) : IDisposable, IClipboardSyncService
{
    private const int DebounceDelayMs = 500;
    private const int InactivityThresholdDays = 14;
    private const int DefaultHistoryLimit = 100;
    private const int DefaultSyncPageSize = 50;
    private const int ShutdownTimeoutSeconds = 5;


    private readonly IClipboardApiService _clipboardApiService =
        clipboardApiService ?? throw new ArgumentNullException(nameof(clipboardApiService));

    private readonly ICryptographyService _cryptographyService =
        cryptographyService ?? throw new ArgumentNullException(nameof(cryptographyService));

    private readonly ILogger<ClipboardSyncService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IClipboardMonitor _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

    private readonly INotificationService _notificationService =
        notificationService ?? throw new ArgumentNullException(nameof(notificationService));

    private readonly IAccountService _accountService =
        accountService ?? throw new ArgumentNullException(nameof(accountService));

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly IClipboardRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));

    private readonly AsyncRetryPolicy _retryPolicy = Policy
        .Handle<HttpRequestException>()
        .Or<WebSocketException>()
        .Or<TimeoutException>()
        .Or<InvalidOperationException>(ex => ex.Message.Contains("WebSocket not connected"))
        .WaitAndRetryAsync(
            3,
            attempt => TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt - 1)),
            (exception, timeSpan, retryCount, context) =>
            {
                logger.LogWarning(exception, $"Retry attempt {retryCount} after {timeSpan.TotalSeconds}s");
            });

    private readonly ISecureStorage _secureStorage =
        secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));

    private readonly ISettingsService _settingsService =
        settingsService ?? throw new ArgumentNullException(nameof(settingsService));

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly IUtils _utils = utils ?? throw new ArgumentNullException(nameof(utils));

    private readonly IWebSocketService _webSocketService =
        webSocketService ?? throw new ArgumentNullException(nameof(webSocketService));

    // ACK tracking for clipboard sync
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocketAckResponse>> _pendingAcks = new();
    // _ackLock removed as _pendingAcks is now thread-safe
    private const int AckTimeoutSeconds = 5;
    
    // Bug #1.2 fix: Track pending uploads to prevent data loss on shutdown
    private readonly ConcurrentDictionary<string, Task> _pendingUploads = new();

    // Unified channel pipeline for both local and remote events (Issue #1 fix)
    private readonly Channel<ClipboardPipelineEvent> _clipboardChannel = Channel.CreateBounded<ClipboardPipelineEvent>(
        new BoundedChannelOptions(MaxQueuedClipboardEvents)
        {
            FullMode = BoundedChannelFullMode.Wait // Critical: Don't drop events, especially deletes
        });
    private const int MaxQueuedClipboardEvents = 100;
    
    // Suppression guard to prevent feedback loop (Bug #1 fix)
    // Using SemaphoreSlim instead of volatile bool for proper async synchronization
    private readonly SemaphoreSlim _remoteUpdateLock = new(1, 1);
    
    // Consumer task tracking for graceful shutdown (Bug #8 fix)
    private Task? _consumerTask;
    
    // Initialization lock to prevent race conditions (Bug #9 fix)
    private readonly SemaphoreSlim _initLock = new(1, 1);
    
    // Master key cache to avoid repeated secure storage I/O (Issue #2 fix)
    private byte[]? _cachedMasterKey;
    private readonly SemaphoreSlim _masterKeyLock = new(1, 1);

    private CancellationTokenSource? _debounceCts;
    private bool _disposed;
    private volatile bool _isInitialized;


    public async Task InitializeAsync()
    {
        // Bug #2.4 fix: Double-check locking optimization
        if (_isInitialized) return;

        // Bug #2.3 fix: Prevent race condition with initialization lock
        await _initLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            try
            {
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
                        RunBackgroundTask(async ct => await RefreshFromServerAsync(DefaultSyncPageSize),
                            "Cold Start Refresh");
                    }
                }

                // Cleanup tombstones on startup (keep DB clean)
                await _repository.PurgeTombstonesAsync();

                // Auto-refresh if database is empty (first launch or after cold start wipe)
                var existingEntries = await _repository.GetAllAsync(1);
                if (existingEntries.Count == 0)
                {
                    _logger.LogInformation("Database is empty, triggering automatic background refresh");
                    RunBackgroundTask(async ct => 
                    {
                        await SyncInBackgroundAsync();
                        // Notify UI to refresh after sync completes
                        OnHistoryUpdated?.Invoke();
                    }, "Auto-Refresh on Empty DB");
                }

                // Subscribe to events
                _monitor.OnClipboardChanged += OnClipboardChanged;
                _webSocketService.OnMessageReceived += OnWebSocketMessageReceived;
                _webSocketService.OnConnected += OnWebSocketConnected;
                _webSocketService.OnDisconnected += OnWebSocketDisconnected;
                _webSocketService.OnError += OnWebSocketError;
                _accountService.OnLogin += OnAccountLoggedIn;
                _accountService.OnLogout += OnAccountLoggedOut;
                _repository.OnDataChanged += () => OnHistoryUpdated?.Invoke();

                _logger.LogInformation("Event subscriptions completed");

                // Connect WebSocket if user is authenticated
                var masterKeyBase64 = await _secureStorage.LoadAsync(_cryptographyService.MasterKey).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(masterKeyBase64))
                {
                    _logger.LogInformation("User is authenticated, ensuring WebSocket connection");
                    if (!_webSocketService.IsConnected) _ = _webSocketService.ConnectAsync();
                }

                // Always start clipboard monitoring
                _logger.LogInformation("Starting clipboard monitor");
                await _monitor.StartAsync();
                _logger.LogInformation($"Clipboard monitor started. IsRunning: {_monitor.IsRunning}");

                // Start consumer task for clipboard channel (Bug #7, #8 fix)
                _consumerTask = Task.Run(() => ProcessClipboardChannelAsync(_shutdownCts.Token), _shutdownCts.Token);
                _logger.LogInformation("Clipboard channel consumer started");

                _isInitialized = true;
                _logger.LogInformation("ClipboardSyncService initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ClipboardSyncService");
                throw;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }


    public event Action? OnHistoryUpdated;

    public async Task<List<ClipboardDbModel>> GetHistoryForUI(int limit = 100)
    {
        try
        {
            var entries = await _repository.GetAllAsync(limit).ConfigureAwait(false);
            return entries ?? new List<ClipboardDbModel>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load history for UI");
            return new List<ClipboardDbModel>();
        }
    }

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
            // Bug #2.1 fix: Ensure UI updates immediately with local data
            OnHistoryUpdated?.Invoke();

            // Trigger background sync to catch up with server
            RunBackgroundTask(async ct => 
            {
                await SyncInBackgroundAsync();
                // Bug #2.1 fix: Ensure UI updates after background sync completes
                OnHistoryUpdated?.Invoke();
            }, "Refresh Sync");

            // Return empty list if no data (don't return null)
            return localEntries ?? new List<ClipboardDbModel>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading clipboard history");
            _ = SyncInBackgroundAsync(); // Try to sync from server anyway
            return new List<ClipboardDbModel>();
        }
        finally
        {
            _refreshLock.Release();
        }
    }



    public async Task DeleteClipboardEntryAsync(string clipboardId)
    {
        if (string.IsNullOrEmpty(clipboardId))
            throw new ArgumentException("Clipboard ID cannot be null or empty", nameof(clipboardId));

        try
        {
            // 1. Mark as deleted locally (tombstone) - UI updates immediately via OnHistoryUpdated
            await _repository.MarkDeletedAsync(clipboardId);

            // 2. Fetch the updated model (which now has IsDeleted=true) and send it
            // This ensures consistent logic with the reconnect loop
            var entry = await _repository.GetByIdAsync(clipboardId);
            if (entry != null)
            {
                await SendExistingEntryAsync(entry);
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"Failed to delete clipboard entry: {ex.Message}");
            _logger.LogError(ex, $"Failed to delete clipboard entry {clipboardId}");
            throw;
        }
    }

    public async Task ShutdownAsync()
    {
        try
        {
            _logger.LogInformation("Starting graceful shutdown...");
            
            // Stop accepting new clipboard events
            _clipboardChannel.Writer.Complete();
            _logger.LogInformation("Clipboard channel writer completed");
            
            // Cancel shutdown token to signal consumer to stop
            _shutdownCts.Cancel();
            
            // Bug #1.2 fix: Wait for pending uploads to complete
            if (!_pendingUploads.IsEmpty)
            {
                _logger.LogInformation($"Waiting for {_pendingUploads.Count} pending uploads to complete...");
                var pendingTasks = _pendingUploads.Values.ToList();
                var uploadTimeout = TimeSpan.FromSeconds(10); // Generous timeout for uploads
                await Task.WhenAny(Task.WhenAll(pendingTasks), Task.Delay(uploadTimeout));
            }
            
            // Bug #8 fix: Await consumer task with timeout
            if (_consumerTask != null)
            {
                var shutdownTimeout = TimeSpan.FromSeconds(ShutdownTimeoutSeconds);
                var completedTask = await Task.WhenAny(_consumerTask, Task.Delay(shutdownTimeout));
                
                if (completedTask == _consumerTask)
                {
                    _logger.LogInformation("Consumer task completed gracefully");
                }
                else
                {
                    _logger.LogWarning($"Consumer task did not complete within {ShutdownTimeoutSeconds}s timeout");
                }
            }

            // Stop clipboard monitor
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
            _webSocketService.OnConnected -= OnWebSocketConnected;
            _webSocketService.OnDisconnected -= OnWebSocketDisconnected;
            _webSocketService.OnError -= OnWebSocketError;
            _accountService.OnLogin -= OnAccountLoggedIn;
            _accountService.OnLogout -= OnAccountLoggedOut;

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();

            _refreshLock.Dispose();
            // _ackLock.Dispose(); // Removed
            _initLock.Dispose();
            _masterKeyLock.Dispose();
            _remoteUpdateLock.Dispose();

            _shutdownCts.Cancel();
            _shutdownCts.Dispose();

            _logger.LogInformation("ClipboardSyncService disposed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disposal");
        }
    }

    private async Task SyncInBackgroundAsync()
    {
        try
        {
            var masterKeyBase64 = await _secureStorage.LoadAsync(_cryptographyService.MasterKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(masterKeyBase64))
            {
                _logger.LogInformation("Sync skipped: User not logged in");
                return;
            }
            
            // Optimize: Decode key once outside the loop
            var masterKey = Convert.FromBase64String(masterKeyBase64);

            var historyResponse = await _clipboardApiService.GetClipboardHistoryAsync(1, DefaultSyncPageSize, includeDeleted: true)
                .ConfigureAwait(false);

            if (historyResponse?.history == null || historyResponse.history.Count == 0)
            {
                _settingsService.Settings.last_sync = DateTime.UtcNow;
                _settingsService.Save();
                return;
            }

            var incomingBatch = new List<ClipboardDbModel>();
            var idsToDelete = new List<string>();

            // Pre-fetch IDs to check for duplicates/updates
            var incomingIds = historyResponse.history.Select(e => e.id).ToList();
            var existingEntriesById = await _repository.GetByIdsAsync(incomingIds);

            foreach (var entry in historyResponse.history)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id)) continue;

                // 1. Handle Deletes (Tombstones)
                if (entry.is_deleted)
                {
                    // Only bother deleting if we actually have it
                    if (existingEntriesById.ContainsKey(entry.id))
                    {
                        idsToDelete.Add(entry.id);
                    }
                    continue;
                }

                // 2. Handle Encryption (Crucial Step missing in previous version)
                string plaintext;
                if (string.IsNullOrEmpty(entry.ciphertext) || string.IsNullOrEmpty(entry.nonce))
                {
                    // Fallback if server somehow sends unencrypted data or corrupt data
                    if (string.IsNullOrEmpty(entry.plaintext)) continue;
                    plaintext = entry.plaintext;
                }
                else
                {
                    try
                    {
                        plaintext = _cryptographyService.DecryptClipboard(
                            _cryptographyService.FromBase64(entry.ciphertext),
                            _cryptographyService.FromBase64(entry.nonce),
                            masterKey);
                    }
                    catch
                    {
                        // Handle decryption failure (Ghost Entry)
                        var safeHash = _utils.ComputeHash(entry.ciphertext);
                        plaintext = $"Encrypted Content Unavailable [{safeHash}-{entry.id}]";
                    }
                }

                var contentHash = _utils.ComputeHash(plaintext);
                
                // Check against ID map
                existingEntriesById.TryGetValue(entry.id, out var existingById);

                // Deduplication Logic:
                // If we have the entry by ID, and the content hash matches, it's a true duplicate. Skip.
                if (existingById != null && existingById.ContentHash == contentHash)
                    continue;

                // Normalize Timestamp
                var serverTimestamp = entry.timestamp.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(entry.timestamp, DateTimeKind.Utc) // Treating Unspecified as UTC
                    : entry.timestamp.ToUniversalTime();

                serverTimestamp = _utils.TruncateToMilliseconds(serverTimestamp);

                var dbEntry = new ClipboardDbModel
                {
                    Id = entry.id,
                    Content = plaintext,
                    ContentHash = contentHash,
                    Ciphertext = entry.ciphertext ?? string.Empty,
                    Nonce = entry.nonce ?? string.Empty,
                    BlobVersion = entry.blob_version,
                    CreatedAt = serverTimestamp,
                    IsSynced = true // It came from server, so it is synced
                };

                incomingBatch.Add(dbEntry);

                // If we have an entry with this ID but content/timestamp differed, we need to overwrite it.
                // SQLite Upsert usually handles this via ID, but explicit delete ensures clean state.
                if (existingById != null)
                    idsToDelete.Add(entry.id);
            }

            // Database Transaction - REMOVED WRAPPER TO PREVENT DEADLOCK
            // Executing sequentially instead of atomic transaction prevents reentrancy deadlock 
            // on the exclusive TaskScheduler.
            if (idsToDelete.Count > 0 || incomingBatch.Count > 0)
            {
                foreach (var id in idsToDelete)
                {
                    var isTombstone = historyResponse.history.FirstOrDefault(x => x.id == id)?.is_deleted ?? false;
                    if (isTombstone)
                        await _repository.MarkDeletedAsync(id).ConfigureAwait(false);
                    else
                        await _repository.DeleteByIdAsync(id).ConfigureAwait(false); 
                }

                if (incomingBatch.Count > 0)
                {
                    await _repository.UpsertAsync(incomingBatch).ConfigureAwait(false);
                }
            }

            _settingsService.Settings.last_sync = DateTime.UtcNow;
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background sync failed");
        }
    }

    private void OnClipboardChanged(string content)
    {
        if (_shutdownCts.IsCancellationRequested) return;

        // Bug #3 fix: Wrap semaphore usage in try/finally to prevent leaks
        var lockAcquired = false;
        try
        {
            // Bug #1 fix: Check suppression guard
            lockAcquired = _remoteUpdateLock.Wait(0);
            if (!lockAcquired)
            {
                _logger.LogDebug("Ignoring clipboard change from remote update (suppression guard active)");
                return;
            }
        }
        finally
        {
            if (lockAcquired) _remoteUpdateLock.Release();
        }

        _logger.LogDebug($"OnClipboardChanged fired with content length: {content?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogDebug("Content is null or whitespace, skipping");
            return;
        }

        // Fix 1: Move debounce logic here to prevent race conditions
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelayMs, token);
                if (token.IsCancellationRequested) return;

                // Bug #2 fix: Check shutdown token again after delay
                if (_shutdownCts.IsCancellationRequested) return;

                // Issue #1 fix: Write local content to unified channel
                var evt = new ClipboardPipelineEvent.LocalUpsert(content, DateTime.UtcNow);
        
                // Bug #2 fix: Protect against channel closure during write
                try 
                {
                     // Defensive check before writing
                    if (!_clipboardChannel.Writer.TryWrite(evt)) // Fast check
                    {
                        await _clipboardChannel.Writer.WriteAsync(evt, token);
                    }
                }
                catch (ChannelClosedException)
                {
                    _logger.LogWarning("Attempted to write to closed clipboard channel");
                }
            }
            catch (OperationCanceledException)
            {
                // Debounced
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in clipboard debounce task");
            }
        });
    }

    // Unified consumer loop - processes both local and remote events sequentially (Issue #1 fix)
    private async Task ProcessClipboardChannelAsync(CancellationToken ct)
    {
        _logger.LogInformation("Unified clipboard channel consumer loop started");
        
        try
        {
            await foreach (var evt in _clipboardChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    // Pattern match on event type
                    switch (evt)
                    {
                        case ClipboardPipelineEvent.LocalUpsert local:
                            // Fix 1: Debounce handled in producer (OnClipboardChanged), just process immediately
                            _logger.LogDebug($"Processing local clipboard upsert from {local.Timestamp:HH:mm:ss.fff}");
                            await ProcessOutgoingClipboardAsync(local.Content);
                            break;


                            
                        case ClipboardPipelineEvent.RemoteEntry remote:
                            _logger.LogDebug($"Processing remote clipboard entry {remote.Entry.id} from {remote.Timestamp:HH:mm:ss.fff}");
                            await ProcessIncomingClipboardAsync(remote.Entry);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process clipboard event");
                    // Continue processing other events
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Clipboard channel consumer loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clipboard channel consumer loop failed");
        }
        
        _logger.LogInformation("Clipboard channel consumer loop stopped");
    }

    // Issue #2 fix: Cache master key to avoid repeated secure storage I/O
    private async Task<byte[]?> GetMasterKeyAsync()
    {
        // Bug #2.4 fix: Double-check locking optimization
        if (_cachedMasterKey != null) return _cachedMasterKey;

        await _masterKeyLock.WaitAsync();
        try
        {
            if (_cachedMasterKey != null)
            {
                return _cachedMasterKey;
            }

            var masterKeyBase64 = await _secureStorage.LoadAsync(_cryptographyService.MasterKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(masterKeyBase64))
            {
                return null;
            }

            _cachedMasterKey = Convert.FromBase64String(masterKeyBase64);
            _logger.LogDebug("Master key cached in memory");
            return _cachedMasterKey;
        }
        finally
        {
            _masterKeyLock.Release();
        }
    }

    /// <summary>
    /// Clears the cached master key. Should be called on logout or key rotation.
    /// </summary>
    public void ClearMasterKeyCache()
    {
        _masterKeyLock.Wait();
        try
        {
            _cachedMasterKey = null;
            _logger.LogDebug("Master key cache cleared");
        }
        finally
        {
            _masterKeyLock.Release();
        }
    }

    private async Task ProcessOutgoingClipboardAsync(string content)
    {
        try
        {
            _logger.LogDebug("ProcessOutgoingClipboardAsync started");

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Content is null or whitespace, skipping");
                return;
            }

            // Clipboard monitoring is always on - process all clipboard changes

            var contentHash = _utils.ComputeHash(content);
            _logger.LogDebug($"Content hash: {contentHash.Substring(0, 16)}...");

            var lastEntries = await _repository.GetAllAsync(1);
            var lastEntry = lastEntries.FirstOrDefault();

            if (lastEntry != null && lastEntry.ContentHash == contentHash)
            {
                _logger.LogDebug("Content matches last entry, skipping duplicate");
                return;
            }

            // Generate deterministic UUID and Timestamp (Client Authority)
            // Truncate to milliseconds for consistent precision
            var clientId = Guid.NewGuid().ToString();
            var timestamp = _utils.TruncateToMilliseconds(DateTime.UtcNow);

            // Issue #2 fix: Use cached master key
            var masterKey = await GetMasterKeyAsync();
            if (masterKey == null)
            {
                _logger.LogWarning("Master key not found - cannot encrypt clipboard content");
                return;
            }

            var (ciphertext, nonce) = _cryptographyService.EncryptClipboard(content, masterKey);

            // Save to SQLite with encrypted data
            var dbEntry = new ClipboardDbModel
            {
                Id = clientId,
                Content = content,
                ContentHash = contentHash,
                Ciphertext = _cryptographyService.ToBase64(ciphertext),
                Nonce = _cryptographyService.ToBase64(nonce),
                BlobVersion = _settingsService.Settings.blob_version,
                CreatedAt = timestamp,
                IsSynced = false // Will be set to true only after server ACK
            };

            await _repository.UpsertAsync(dbEntry);

            // Send to server
            await SendExistingEntryAsync(dbEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clipboard processing error");
            _notificationService.ShowError($"Clipboard processing error: {ex.Message}");
        }
    }



    private Task SendExistingEntryAsync(ClipboardDbModel entry)
    {
        // Skip if already synced
        if (entry.IsSynced) return Task.CompletedTask;

        try
        {
            var uploadTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingUploads[entry.Id] = uploadTcs.Task;

            RunBackgroundTask(async ct =>
            {
                try
                {
                    if (!_webSocketService.IsConnected) return;

                    await _retryPolicy.ExecuteAsync(async () =>
                    {
                        if (!await _webSocketService.EnsureConnectedAsync(TimeSpan.FromSeconds(5)))
                            throw new InvalidOperationException("WebSocket not connected");

                        var isTombstone = entry.IsDeleted;
                        if (isTombstone && entry.IsSynced) return;
                        _logger.LogInformation($"Sending {(isTombstone ? "tombstone" : "entry")} via WebSocket: {entry.Id}");

                        var ackTcs = new TaskCompletionSource<WebSocketAckResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                        
                            // Bug #1 fix: Use thread-safe dictionary without explicit lock
                            _pendingAcks[entry.Id] = ackTcs;

                            try
                            {
                                var request = new ClipboardSyncRequest
                                {
                                    id = entry.Id,
                                    timestamp = entry.CreatedAt,
                                    blob_version = entry.BlobVersion,
                                    is_deleted = isTombstone, // <--- CRITICAL FIX
                                    // If deleted, send null payload. If upsert, send ciphertext.
                                    ciphertext = isTombstone ? null : entry.Ciphertext,
                                    nonce = isTombstone ? null : entry.Nonce,
                                };

                                await _webSocketService.SendMessageAsync(request);

                                var ackTask = ackTcs.Task;
                                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(AckTimeoutSeconds));
                                
                                if (await Task.WhenAny(ackTask, timeoutTask) == ackTask)
                                {
                                    await ackTask; // Propagate exceptions if any
                                    _logger.LogInformation($"Received ACK for {entry.Id}");
                                    await _repository.MarkAsSyncedAsync(entry.Id);
                                }
                                else
                                {
                                    // Bug #4 fix: Explicitly handle timeout with Retry
                                    throw new TimeoutException($"ACK timeout for {entry.Id}");
                                }
                            }
                            catch (TimeoutException)
                            {
                                _logger.LogWarning($"ACK timed out for {entry.Id} - entry remains unsynced");

                                // Schedule retry
                                RunBackgroundTask(async ct =>
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                                    if (!_shutdownCts.IsCancellationRequested)
                                    {
                                        await SendExistingEntryAsync(entry);
                                    }
                                }, "Retry Unsynced Entry");
                                throw; // Propagate to outer logger
                            }
                            finally
                            {
                                _pendingAcks.TryRemove(entry.Id, out _);
                            }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send {entry.Id}");
                }
                finally
                {
                    _pendingUploads.TryRemove(entry.Id, out _);
                    uploadTcs.TrySetResult();
                }
            }, $"Send {entry.Id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to initiate send for {entry.Id}");
        }
        return Task.CompletedTask;
    }

    // GenerateClientId method removed as it is no longer used


    private void OnWebSocketMessageReceived(string message)
    {
        // Try to parse as ACK first
        RunBackgroundTask(async ct => await ProcessWebSocketMessageAsync(message, ct), "WebSocket Message");
    }

    private async Task ProcessWebSocketMessageAsync(string message, CancellationToken ct = default)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                var type = typeProp.GetString();
                
                if (type == "ack")
                {
                    var ackResponse = JsonSerializer.Deserialize<WebSocketAckResponse>(message);
                    if (ackResponse != null)
                    {
                        _logger.LogDebug($"Received ACK for entry {ackResponse.id}");
                        // Bug #1 fix: Thread-safe removal/access
                        if (_pendingAcks.TryGetValue(ackResponse.id, out var tcs))
                        {
                            tcs.TrySetResult(ackResponse);
                        }
                    }
                    return;
                }
                else if (type == "error")
                {
                    _logger.LogWarning($"Received server error: {message}");
                    return;
                }
                else if (type == "pong")
                {
                    // Pong handled implicitly or ignored
                    return;
                }
            }

            // Not an ACK/Error/Pong - deserialize as clipboard entry (includes tombstones)
            var entry = JsonSerializer.Deserialize<ClipboardEntry>(message);
            if (entry == null)
            {
                _logger.LogWarning("Failed to deserialize WebSocket message");
                return;
            }

            // Route remote entry through unified channel (handles both upserts and deletes)
            var evt = new ClipboardPipelineEvent.RemoteEntry(entry, DateTime.UtcNow);
            
            try
            {
                await _clipboardChannel.Writer.WriteAsync(evt, ct);
            }
            catch (ChannelClosedException)
            {
                 _logger.LogDebug("Clipboard channel closed; dropping remote event");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process WebSocket message");
        }
    }

    private async Task ProcessIncomingClipboardAsync(ClipboardEntry entry)
    {
        try
        {
            // Validate required fields
            if (entry == null || string.IsNullOrEmpty(entry.id))
            {
                _logger.LogDebug("Invalid clipboard entry - missing ID");
                return;
            }

            var timestamp = entry.timestamp.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(entry.timestamp, DateTimeKind.Utc)
                : entry.timestamp.ToUniversalTime();
            
            entry.timestamp = _utils.TruncateToMilliseconds(timestamp);

            // Handle tombstones (deleted entries) - they have null ciphertext/nonce
            if (entry.is_deleted)
            {
                // Fix: Always honor deletions regardless of timestamp check to prevent zombie entries
                _logger.LogInformation($"Processing remote tombstone for {entry.id} (IS_DELETED=TRUE)");
                await _repository.MarkDeletedAsync(entry.id);
                return;
            }

            // For non-deleted entries, ciphertext and nonce are required
            if (string.IsNullOrEmpty(entry.ciphertext) || string.IsNullOrEmpty(entry.nonce))
            {
                _logger.LogWarning($"Invalid clipboard entry {entry.id} - missing ciphertext or nonce for non-deleted entry");
                return;
            }

            // Issue #2 fix: Use cached master key
            var masterKey = await GetMasterKeyAsync();
            if (masterKey == null)
            {
                _logger.LogWarning("Master key not available, user may not be authenticated");
                return;
            }

            var ciphertext = _cryptographyService.FromBase64(entry.ciphertext);
            var nonce = _cryptographyService.FromBase64(entry.nonce);

            // Decrypt content with error handling
            string plaintext;
            try
            {
                plaintext = _cryptographyService.DecryptClipboard(ciphertext, nonce, masterKey);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Security.Cryptography.CryptographicException)
            {
                _logger.LogError(ex, $"Failed to decrypt clipboard entry {entry.id} - data may be corrupted or tampered");
                
                // Bug #2.2 fix: Save placeholder for failed decryption (Ghost Entries)
                // Fix 2: Include ciphertext hash in plaintext to ensure unique contentHash for different failed entries
                var safeHash = _utils.ComputeHash(entry.ciphertext ?? string.Empty);
                plaintext = $"Encrypted Content Unavailable [{safeHash}]";
            }
            
            var contentHash = _utils.ComputeHash(plaintext);

            var existingEntry = await _repository.GetByHashAsync(contentHash);
            if (existingEntry != null)
            {
                // If we already have the exact same ID, skip
                if (existingEntry.Id == entry.id)
                {
                    _logger.LogDebug($"Skipping duplicate clipboard entry: {entry.id}");
                    return;
                }
                // If we have a local entry with different ID but same content, 
                // replace it with the authoritative version from server

                _logger.LogInformation($"Replacing local entry {existingEntry.Id} with authoritative entry {entry.id}");
            }

            // Timestamp already normalized at start of method
            var serverTimestamp = entry.timestamp;

            // Save to SQLite
            var dbEntry = new ClipboardDbModel
            {
                Id = entry.id,
                Content = plaintext,
                ContentHash = contentHash,
                Ciphertext = entry.ciphertext ?? string.Empty,
                Nonce = entry.nonce ?? string.Empty,
                BlobVersion = entry.blob_version,
                CreatedAt = serverTimestamp,
                IsSynced = true // Entries from server are already synced
            };

            // Issue #3 fix: Wrap delete+upsert in transaction to prevent data loss
            // DEADLOCK FIX: RunInTransactionAsync removed. Executing sequentially.
            if (existingEntry != null && existingEntry.Id != entry.id)
            {
                await _repository.DeleteByIdAsync(existingEntry.Id).ConfigureAwait(false);
            }
            await _repository.UpsertAsync(dbEntry).ConfigureAwait(false);

            // Bug #1 fix: Set suppression guard before writing to OS clipboard using semaphore
            await _remoteUpdateLock.WaitAsync();
            try
            {
                // Bug #1.1 fix: Dispatch to UI thread to prevent STA crash
                await Dispatcher.UIThread.InvokeAsync(async () => 
                {
                    await _monitor.SetClipboardTextAsync(plaintext);
                });
            }
            finally
            {
                _remoteUpdateLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process WebSocket message");
        }
    }

    private void OnWebSocketDisconnected()
    {
        _logger.LogWarning("WebSocket disconnected - clipboard sync paused until reconnection");
        
        if (_pendingAcks.IsEmpty) return;

        _logger.LogWarning($"Clearing {_pendingAcks.Count} pending ACKs due to disconnect");

        // Bug #3 fix: Clear pending ACKs to prevent stuck tasks
        foreach (var kvp in _pendingAcks)
        {
             kvp.Value.TrySetCanceled();
        }
        _pendingAcks.Clear();
    }
        


    private void OnWebSocketConnected()
    {
         RunBackgroundTask(async ct =>
        {
            try
            {
                _logger.LogInformation("WebSocket reconnected, retrying unsynced entries");
                var unsyncedEntries = await _repository.GetUnsyncedAsync();
                
                if (unsyncedEntries.Count > 0)
                {
                    _logger.LogInformation($"Found {unsyncedEntries.Count} unsynced entries to retry");
                    foreach (var entry in unsyncedEntries)
                    {
                        await SendExistingEntryAsync(entry);
                    }
                }
                else
                {
                    _logger.LogInformation("No unsynced entries to retry");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retry unsynced entries on reconnect");
            }
        }, "Reconnect Retry");
    }

    private void OnWebSocketError(string errorMessage)
    {
        _logger.LogWarning($"WebSocket error: {errorMessage}");
    }

    private void RunBackgroundTask(Func<CancellationToken, Task> taskFunc, string taskName = "Background Task")
    {
        var cts = _shutdownCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await taskFunc(cts).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"{taskName} was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{taskName} failed: {ex.GetType().Name}");
            }
        }, cts);
    }

    private async Task OnAccountLoggedIn()
    {
        _logger.LogInformation("User logged in, triggering automatic refresh from server");
        
        // Trigger background refresh to fetch clipboard history from server
        RunBackgroundTask(async ct => 
        {
            await SyncInBackgroundAsync();
            // Notify UI to refresh after sync completes
            OnHistoryUpdated?.Invoke();
        }, "Auto-Refresh on Login");
    }

    private async Task OnAccountLoggedOut()
    {
        ClearMasterKeyCache();
        
        // Wipe local database completely on logout/failure to ensure fresh state on next login
        await _repository.ClearAllAsync();
        
        // Disconnect WebSocket
        await _webSocketService.DisconnectAsync();
    }
}