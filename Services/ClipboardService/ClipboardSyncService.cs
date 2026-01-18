using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Synclo.Models;
using Synclo.Services.SecretsManager;

namespace Synclo.Services.ClipboardService;

// Unified pipeline event - supports both local content and remote entries
internal abstract record ClipboardPipelineEvent
{
    public record LocalContent(string Content, DateTime Timestamp) : ClipboardPipelineEvent;
    public record RemoteEntry(ClipboardEntry Entry, DateTime Timestamp) : ClipboardPipelineEvent;
    public record RemoteDelete(string Id, DateTime Timestamp) : ClipboardPipelineEvent;
}

public interface IClipboardSyncService : IDisposable
{
    Task InitializeAsync();
    Task<List<ClipboardDbModel>> GetHistoryForUI(int limit = 100);
    Task<IReadOnlyList<ClipboardDbModel>> RefreshFromServerAsync(int limit = 100);
    Task SetEnabledAsync(bool enabled);
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
    private const int MinRefreshIntervalMs = 300;

    private readonly IClipboardApiService _clipboardApiService =
        clipboardApiService ?? throw new ArgumentNullException(nameof(clipboardApiService));

    private readonly ICryptographyService _cryptographyService =
        cryptographyService ?? throw new ArgumentNullException(nameof(cryptographyService));

    private readonly ILogger<ClipboardSyncService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IClipboardMonitor _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

    private readonly INotificationService _notificationService =
        notificationService ?? throw new ArgumentNullException(nameof(notificationService));

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
    private readonly Dictionary<string, TaskCompletionSource<WebSocketAckResponse>> _pendingAcks = new();
    private readonly SemaphoreSlim _ackLock = new(1, 1);
    private const int AckTimeoutSeconds = 5;

    // Unified channel pipeline for both local and remote events (Issue #1 fix)
    private readonly Channel<ClipboardPipelineEvent> _clipboardChannel = Channel.CreateBounded<ClipboardPipelineEvent>(
        new BoundedChannelOptions(MaxQueuedClipboardEvents)
        {
            FullMode = BoundedChannelFullMode.DropOldest // Drop oldest events if queue is full
        });
    private const int MaxQueuedClipboardEvents = 100;
    
    // Suppression guard to prevent feedback loop (Bug #1 fix)
    private volatile bool _isRemoteUpdate = false;
    
    // Consumer task tracking for graceful shutdown (Bug #8 fix)
    private Task? _consumerTask;
    
    // Initialization lock to prevent race conditions (Bug #9 fix)
    private readonly SemaphoreSlim _initLock = new(1, 1);
    
    // Master key cache to avoid repeated secure storage I/O (Issue #2 fix)
    private byte[]? _cachedMasterKey;
    private readonly SemaphoreSlim _masterKeyLock = new(1, 1);

    private CancellationTokenSource? _debounceCts;
    private bool _disposed;
    private bool _isInitialized;
    private DateTime _lastRefreshTime = DateTime.MinValue;

    public async Task InitializeAsync()
    {
        // Bug #9 fix: Prevent race condition with initialization lock
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

                // Auto-refresh if database is empty (first launch or after cold start wipe)
                var existingEntries = await _repository.GetAllAsync(1);
                if (existingEntries.Count == 0)
                {
                    _logger.LogInformation("Database is empty, triggering automatic background refresh");
                    RunBackgroundTask(async ct => await SyncInBackgroundAsync(), "Auto-Refresh on Empty DB");
                }

                // Subscribe to events
                _monitor.OnClipboardChanged += OnClipboardChanged;
                _webSocketService.OnMessageReceived += OnWebSocketMessageReceived;
                _webSocketService.OnDisconnected += OnWebSocketDisconnected;
                _webSocketService.OnError += OnWebSocketError;
                _repository.OnDataChanged += () => OnHistoryUpdated?.Invoke();

                _logger.LogInformation("Event subscriptions completed");

                // Connect WebSocket if user is authenticated
                var masterKeyBase64 = await _secureStorage.LoadAsync(_cryptographyService.MasterKey).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(masterKeyBase64))
                {
                    _logger.LogInformation("User is authenticated, ensuring WebSocket connection");
                    if (!_webSocketService.IsConnected) _ = _webSocketService.ConnectAsync();
                }

                // Start monitoring if auto-sync is enabled
                if (_settingsService.Settings.auto_sync_enabled)
                {
                    _logger.LogInformation("Auto-sync enabled, starting clipboard monitor");
                    await _monitor.StartAsync();
                    _logger.LogInformation($"Clipboard monitor started. IsRunning: {_monitor.IsRunning}");
                }
                else
                {
                    _logger.LogWarning("Auto-sync is DISABLED - clipboard monitor will NOT start");
                }

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

            // Trigger background sync to catch up with server
            _ = SyncInBackgroundAsync();

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

    public async Task SetEnabledAsync(bool enabled)
    {
        // Bug #10 fix: Persist settings so preference survives restart
        _settingsService.Settings.auto_sync_enabled = enabled;
        _settingsService.Save();
        
        if (enabled)
            await _monitor.StartAsync();
        else
            await _monitor.StopAsync();
    }

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
            _webSocketService.OnDisconnected -= OnWebSocketDisconnected;
            _webSocketService.OnError -= OnWebSocketError;

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();

            _refreshLock.Dispose();
            _ackLock.Dispose();
            _initLock.Dispose();
            _masterKeyLock.Dispose();

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
                return; // Not logged in
            }

            var historyResponse = await _clipboardApiService.GetClipboardHistoryAsync(1, DefaultSyncPageSize, includeDeleted: true)
                .ConfigureAwait(false);

            // Handle null or empty response gracefully
            if (historyResponse == null || historyResponse.history == null || historyResponse.history.Count == 0)
            {
                _logger.LogInformation("Sync completed: No history from server (empty account or new user)");
                _settingsService.Settings.last_sync = DateTime.UtcNow;
                _settingsService.Save();
                return;
            }

            // First pass: Collect all valid entries and their hashes
            var hashToEntry = new Dictionary<string, ClipboardEntry>();
            foreach (var entry in historyResponse.history)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id) || string.IsNullOrWhiteSpace(entry.plaintext))
                    continue;

                var contentHash = _utils.ComputeHash(entry.plaintext);
                hashToEntry[contentHash] = entry;
            }

            // Issue #4 fix: Bulk fetch existing entries by hash (avoid N+1 queries)
            var existingEntries = await _repository.GetHashMapAsync(hashToEntry.Keys);

            var incomingBatch = new List<ClipboardDbModel>();
            // Process each entry
            foreach (var (contentHash, entry) in hashToEntry)
            {
                var existingEntry = existingEntries.GetValueOrDefault(contentHash);

                if (existingEntry != null && existingEntry.Id == entry.id)
                    continue;

                // Normalize server timestamp to UTC and truncate to milliseconds
                var serverTimestamp = entry.timestamp.Kind == DateTimeKind.Utc
                    ? entry.timestamp
                    : entry.timestamp.ToUniversalTime();
                serverTimestamp = _utils.TruncateToMilliseconds(serverTimestamp);
                var createdAt = existingEntry?.CreatedAt ?? serverTimestamp;


                // Handle tombstones (entries synced as deleted)
                if (entry.is_deleted)
                {
                    _logger.LogInformation($"Sync encountered tombstone for {entry.id}, ensuring local deletion");
                    
                    // Delete locally if exists
                    if (existingEntry != null || await _repository.GetByIdAsync(entry.id) != null)
                    {
                        await _repository.DeleteByIdAsync(entry.id);
                    }
                    continue; // Skip upsert for deleted items
                }

                var dbEntry = new ClipboardDbModel
                {
                    Id = entry.id,
                    Content = entry.plaintext,
                    ContentHash = contentHash,
                    Ciphertext = entry.ciphertext ?? string.Empty,
                    Nonce = entry.nonce ?? string.Empty,
                    BlobVersion = entry.blob_version,
                    CreatedAt = createdAt,
                    IsSynced = true // Entries from server are already synced
                };

                incomingBatch.Add(dbEntry);

                if (existingEntry != null && existingEntry.Id != entry.id)
                    await _repository.DeleteByIdAsync(existingEntry.Id);
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
        catch (HttpRequestException ex)
        {
            // Network error - don't spam user with notifications
            _logger.LogWarning(ex, "Sync failed: Network error");
        }
        catch (Exception ex)
        {
            // Unexpected error - log it
            _logger.LogError(ex, $"Sync failed: {ex.GetType().Name}");
        }
    }

    private void OnClipboardChanged(string content)
    {
        // Bug #1 fix: Check suppression guard to prevent feedback loop
        if (_isRemoteUpdate)
        {
            _logger.LogDebug("Ignoring clipboard change from remote update (suppression guard active)");
            return;
        }

        _logger.LogDebug($"OnClipboardChanged fired with content length: {content?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogDebug("Content is null or whitespace, skipping");
            return;
        }

        // Issue #1 fix: Write local content to unified channel
        var evt = new ClipboardPipelineEvent.LocalContent(content, DateTime.UtcNow);
        
        if (!_clipboardChannel.Writer.TryWrite(evt))
        {
            _logger.LogWarning("Clipboard channel is full, oldest event will be dropped");
        }
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
                        case ClipboardPipelineEvent.LocalContent local:
                            // Issue #5 fix: Debounce in consumer loop
                            // Wait for potential rapid-fire updates
                            await Task.Delay(DebounceDelayMs, ct);
                            
                            // If more events arrived while we were waiting, this one is superseded
                            if (_clipboardChannel.Reader.Count > 0)
                            {
                                _logger.LogDebug($"Skipping superseded local event from {local.Timestamp:HH:mm:ss.fff}");
                                continue;
                            }

                            _logger.LogDebug($"Processing local clipboard event from {local.Timestamp:HH:mm:ss.fff}");
                            await ProcessOutgoingClipboardAsync(local.Content);
                            break;
                            
                        case ClipboardPipelineEvent.RemoteEntry remote:
                            _logger.LogDebug($"Processing remote clipboard entry {remote.Entry.id} from {remote.Timestamp:HH:mm:ss.fff}");
                            _logger.LogDebug($"Processing remote clipboard entry {remote.Entry.id} from {remote.Timestamp:HH:mm:ss.fff}");
                            await ProcessIncomingClipboardAsync(remote.Entry);
                            break;

                        case ClipboardPipelineEvent.RemoteDelete remoteDelete:
                            _logger.LogDebug($"Processing remote delete for {remoteDelete.Id}");
                            await _repository.DeleteByIdAsync(remoteDelete.Id);
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

    private async Task ProcessOutgoingClipboardAsync(string content)
    {
        try
        {
            _logger.LogInformation("ProcessOutgoingClipboardAsync started");

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Content is null or whitespace, skipping");
                return;
            }

            if (!_settingsService.Settings.auto_sync_enabled)
            {
                _logger.LogInformation("Auto-sync disabled, skipping");
                return;
            }

            var contentHash = _utils.ComputeHash(content);
            _logger.LogInformation($"Content hash: {contentHash.Substring(0, 16)}...");

            var lastEntries = await _repository.GetAllAsync(1);
            var lastEntry = lastEntries.FirstOrDefault();

            if (lastEntry != null && lastEntry.ContentHash == contentHash)
            {
                _logger.LogInformation("Content matches last entry, skipping duplicate");
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

    private async Task SendExistingEntryAsync(ClipboardDbModel entry)
    {
        // Skip if already synced
        if (entry.IsSynced)
        {
            _logger.LogDebug($"Entry {entry.Id} is already synced, skipping");
            return;
        }

        try
        {
            RunBackgroundTask(async ct =>
                {
                    try
                    {
                        await _retryPolicy.ExecuteAsync(async () =>
                        {
                            // Ensure WebSocket is connected with timeout
                            if (!await _webSocketService.EnsureConnectedAsync(TimeSpan.FromSeconds(5)))
                            {
                                _logger.LogWarning(
                                    $"WebSocket connection timeout - entry {entry.Id} will retry on next attempt");
                                throw new InvalidOperationException("WebSocket not connected");
                            }

                            _logger.LogInformation($"Sending clipboard entry via WebSocket: {entry.Id}");

                            // Create TaskCompletionSource for ACK
                            var ackTcs = new TaskCompletionSource<WebSocketAckResponse>();
                            
                            await _ackLock.WaitAsync();
                            try
                            {
                                _pendingAcks[entry.Id] = ackTcs;
                            }
                            finally
                            {
                                _ackLock.Release();
                            }

                            try
                            {
                                // Send the clipboard entry
                                var request = new ClipboardSyncRequest
                                {
                                    id = entry.Id,
                                    timestamp = entry.CreatedAt,
                                    ciphertext = entry.Ciphertext,
                                    nonce = entry.Nonce,
                                    blob_version = entry.BlobVersion
                                };

                                await _webSocketService.SendMessageAsync(request);
                                _logger.LogInformation($"Clipboard entry sent via WebSocket: {entry.Id}, waiting for ACK...");

                                // Wait for ACK with timeout
                                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AckTimeoutSeconds));
                                try
                                {
                                    var ackTask = ackTcs.Task;
                                    var completedTask = await Task.WhenAny(ackTask, Task.Delay(Timeout.Infinite, cts.Token));

                                    if (completedTask == ackTask)
                                    {
                                        var ack = await ackTask;
                                        _logger.LogInformation($"Received ACK for entry {entry.Id}");

                                        // Update database to mark as synced (use efficient method)
                                        await _repository.MarkAsSyncedAsync(entry.Id);
                                        _logger.LogInformation($"Entry {entry.Id} marked as synced in database");
                                    }
                                    else
                                    {
                                        throw new TimeoutException($"ACK timeout for entry {entry.Id} after {AckTimeoutSeconds}s");
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw new TimeoutException($"ACK timeout for entry {entry.Id} after {AckTimeoutSeconds}s");
                                }
                            }
                            finally
                            {
                                // Clean up pending ACK (Bug #3 fix: always cleanup even if SendMessageAsync throws)
                                await _ackLock.WaitAsync();
                                try
                                {
                                    _pendingAcks.Remove(entry.Id);
                                }
                                finally
                                {
                                    _ackLock.Release();
                                }
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to send and confirm entry {entry.Id} - will retry on reconnect");
                    }
                }, $"Send Entry: {entry.Id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to initiate send for entry {entry.Id}");
        }
    }

    // GenerateClientId method removed as it is no longer used


    private void OnWebSocketMessageReceived(string message)
    {
        // Try to parse as ACK first
        RunBackgroundTask(async ct => await ProcessWebSocketMessageAsync(message), "WebSocket Message");
    }

    private async Task ProcessWebSocketMessageAsync(string message)
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
                        await _ackLock.WaitAsync();
                        try
                        {
                            if (_pendingAcks.TryGetValue(ackResponse.id, out var tcs))
                            {
                                tcs.TrySetResult(ackResponse);
                                _pendingAcks.Remove(ackResponse.id);
                            }
                        }
                        finally
                        {
                            _ackLock.Release();
                        }
                    }
                    return;
                }
                else if (type == "delete")
                {
                    var deleteResponse = JsonSerializer.Deserialize<WebSocketDeleteResponse>(message);
                    if (deleteResponse != null)
                    {
                        _logger.LogInformation($"Received delete event for {deleteResponse.id}");
                        
                        var deleteEvt = new ClipboardPipelineEvent.RemoteDelete(deleteResponse.id, DateTime.UtcNow);
                        if (!_clipboardChannel.Writer.TryWrite(deleteEvt))
                        {
                            _logger.LogWarning($"Clipboard channel is full, remote delete {deleteResponse.id} will be dropped");
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

            // Not an ACK/Delete/Error - deserialize as clipboard entry
            var entry = JsonSerializer.Deserialize<ClipboardEntry>(message);
            if (entry == null)
            {
                _logger.LogWarning("Failed to deserialize WebSocket message");
                return;
            }

            // Issue #1 fix: Route remote entry through unified channel
            var evt = new ClipboardPipelineEvent.RemoteEntry(entry, DateTime.UtcNow);
            
            if (!_clipboardChannel.Writer.TryWrite(evt))
            {
                _logger.LogWarning($"Clipboard channel is full, remote entry {entry.id} will be dropped");
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

            // Validate required fields - if missing, this is not a clipboard message
            if (entry == null ||
                string.IsNullOrEmpty(entry.id) ||
                string.IsNullOrEmpty(entry.ciphertext) ||
                string.IsNullOrEmpty(entry.nonce))
            {
                _logger.LogDebug("Invalid clipboard entry format - missing required fields");
                return;
            }

            // Issue #2 fix: Use cached master key
            var masterKey = await GetMasterKeyAsync();
            if (masterKey == null)
            {
                _logger.LogWarning("Master key not available, user may not be authenticated");
                return;
            }

            var ciphertext = _cryptographyService.FromBase64(entry.ciphertext ?? string.Empty);
            var nonce = _cryptographyService.FromBase64(entry.nonce ?? string.Empty);

            // Decrypt content with error handling
            string plaintext;
            try
            {
                plaintext = _cryptographyService.DecryptClipboard(ciphertext, nonce, masterKey);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Security.Cryptography.CryptographicException)
            {
                _logger.LogError(ex, $"Failed to decrypt clipboard entry {entry.id} - data may be corrupted or tampered");
                return; // Skip this entry but continue processing others
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

            // Normalize server timestamp to UTC and truncate to milliseconds
            var serverTimestamp = entry.timestamp.Kind == DateTimeKind.Utc
                ? entry.timestamp
                : entry.timestamp.ToUniversalTime();
            serverTimestamp = _utils.TruncateToMilliseconds(serverTimestamp);

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
            await _repository.RunInTransactionAsync(async () =>
            {
                if (existingEntry != null && existingEntry.Id != entry.id)
                {
                    await _repository.DeleteByIdAsync(existingEntry.Id);
                }
                await _repository.UpsertAsync(dbEntry);
            });

            // Bug #1 fix: Set suppression guard before writing to OS clipboard
            _isRemoteUpdate = true;
            try
            {
                await _monitor.SetClipboardTextAsync(plaintext);
            }
            finally
            {
                _isRemoteUpdate = false;
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
        
        // Bug #3 fix: Clear pending ACKs to prevent stuck tasks
        RunBackgroundTask(async ct =>
        {
            await _ackLock.WaitAsync();
            try
            {
                if (_pendingAcks.Count > 0)
                {
                    _logger.LogWarning($"Clearing {_pendingAcks.Count} pending ACKs due to disconnect");
                    foreach (var tcs in _pendingAcks.Values)
                    {
                        tcs.TrySetCanceled();
                    }
                    _pendingAcks.Clear();
                }
            }
            finally
            {
                _ackLock.Release();
            }
        }, "Clear Pending ACKs");
        
        // On reconnect, retry unsynced entries
        RunBackgroundTask(async ct =>
        {
            try
            {
                // Wait a bit for reconnection
                await Task.Delay(2000, ct);
                
                if (_webSocketService.IsConnected)
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
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Reconnect retry cancelled");
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
}