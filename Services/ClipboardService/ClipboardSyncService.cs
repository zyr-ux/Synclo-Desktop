using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Synclo.Models;
using Synclo.Services.SecretsManager;

namespace Synclo.Services.ClipboardService;

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

    private readonly HashSet<string> _inflightEntries = new(); // Track entries being sent to prevent duplicates
    private readonly SemaphoreSlim _inflightLock = new(1, 1);
    private readonly ILogger<ClipboardSyncService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IClipboardMonitor _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

    private readonly INotificationService _notificationService =
        notificationService ?? throw new ArgumentNullException(nameof(notificationService));

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly IClipboardRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));

    private readonly AsyncRetryPolicy _retryPolicy = Policy
        .Handle<Exception>()
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

    private CancellationTokenSource? _debounceCts;
    private bool _disposed;
    private bool _isInitialized;
    private DateTime _lastRefreshTime = DateTime.MinValue;

    public async Task InitializeAsync()
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


            _isInitialized = true;
            _logger.LogInformation("ClipboardSyncService initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ClipboardSyncService");
            throw;
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
            _shutdownCts.Cancel();

            await Task.Delay(1000);

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
            _inflightLock.Dispose();

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

            var historyResponse = await _clipboardApiService.GetClipboardHistoryAsync(1, DefaultSyncPageSize)
                .ConfigureAwait(false);

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

                var contentHash = _utils.ComputeHash(entry.plaintext);
                var existingEntry = await _repository.GetByHashAsync(contentHash);

                if (existingEntry != null && existingEntry.Id == entry.id)
                    continue;

                // Normalize server timestamp to UTC and truncate to milliseconds
                var serverTimestamp = entry.timestamp.Kind == DateTimeKind.Utc
                    ? entry.timestamp
                    : entry.timestamp.ToUniversalTime();
                serverTimestamp = _utils.TruncateToMilliseconds(serverTimestamp);
                var createdAt = existingEntry?.CreatedAt ?? serverTimestamp;

                var dbEntry = new ClipboardDbModel
                {
                    Id = entry.id,
                    Content = entry.plaintext,
                    ContentHash = contentHash,
                    Ciphertext = entry.ciphertext ?? string.Empty,
                    Nonce = entry.nonce ?? string.Empty,
                    BlobVersion = entry.blob_version,
                    CreatedAt = createdAt
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
        _logger.LogInformation($"OnClipboardChanged fired with content length: {content?.Length ?? 0}");

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
                _logger.LogInformation($"Starting debounce delay of {DebounceDelayMs}ms");
                await Task.Delay(DebounceDelayMs, cts.Token).ConfigureAwait(false);
                _logger.LogInformation("Debounce complete, processing outgoing clipboard");
                await ProcessOutgoingClipboardAsync(content).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Debounce cancelled");
            }
        }, "Clipboard Changed");
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

            // Encrypt the content BEFORE saving to database
            var masterKeyBase64 = await _secureStorage.LoadAsync(_cryptographyService.MasterKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(masterKeyBase64))
            {
                _logger.LogWarning("Master key not found - cannot encrypt clipboard content");
                return;
            }

            var masterKey = _cryptographyService.FromBase64(masterKeyBase64);
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
                CreatedAt = timestamp
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
        // Check if already in-flight
        await _inflightLock.WaitAsync();
        try
        {
            if (_inflightEntries.Contains(entry.Id))
            {
                _logger.LogDebug($"Entry {entry.Id} is already being sent, skipping duplicate");
                return;
            }

            _inflightEntries.Add(entry.Id);
        }
        finally
        {
            _inflightLock.Release();
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
                                    $"WebSocket connection timeout - entry {entry.Id} will sync when connection is restored");
                                return;
                            }

                            _logger.LogInformation($"Sending clipboard entry via WebSocket: {entry.Id}");

                            // Use the already-encrypted data from the database entry
                            // No need to re-encrypt - the content was encrypted when saved to DB
                            var request = new ClipboardSyncRequest
                            {
                                id = entry.Id,
                                timestamp = entry.CreatedAt,
                                ciphertext = entry.Ciphertext, // ✅ Use stored ciphertext
                                nonce = entry.Nonce,            // ✅ Use stored nonce
                                blob_version = entry.BlobVersion
                            };

                            // Send via WebSocket with automatic serialization
                            await _webSocketService.SendMessageAsync(request);

                            _logger.LogInformation($"Clipboard entry sent via WebSocket successfully: {entry.Id}");
                        });
                    }
                    finally
                    {
                        // Remove from in-flight tracking
                        await _inflightLock.WaitAsync();
                        try
                        {
                            _inflightEntries.Remove(entry.Id);
                        }
                        finally
                        {
                            _inflightLock.Release();
                        }
                    }
                }, $"Send Entry: {entry.Id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send entry {entry.Id}");
            // Remove from in-flight on error
            await _inflightLock.WaitAsync();
            try
            {
                _inflightEntries.Remove(entry.Id);
            }
            finally
            {
                _inflightLock.Release();
            }
        }
    }

    // GenerateClientId method removed as it is no longer used


    private void OnWebSocketMessageReceived(string message)
    {
        // Process incoming clipboard updates in background
        RunBackgroundTask(async ct => await ProcessIncomingClipboardAsync(message), "WebSocket Message");
    }

    private async Task ProcessIncomingClipboardAsync(string message)
    {
        try
        {
            // Deserialize as clipboard entry (protocol messages already filtered by WebSocketService)
            ClipboardEntry? entry;
            try
            {
                entry = _clipboardApiService.Deserialize<ClipboardEntry>(message);
            }
            catch (Exception ex)
            {
                // Not a valid clipboard entry
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
            var masterKeyBase64 = await _secureStorage.LoadAsync(_cryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
            {
                _logger.LogWarning("Master key not found - cannot decrypt clipboard content");
                return;
            }

            var masterKey = _cryptographyService.FromBase64(masterKeyBase64);
            var ciphertext = _cryptographyService.FromBase64(entry.ciphertext ?? string.Empty);
            var nonce = _cryptographyService.FromBase64(entry.nonce ?? string.Empty);

            var plaintext = _cryptographyService.DecryptClipboard(ciphertext, nonce, masterKey);
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
                await _repository.DeleteByIdAsync(existingEntry.Id);
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
                CreatedAt = serverTimestamp
            };

            await _repository.UpsertAsync(dbEntry);

            // Write to OS clipboard via cross-platform monitor abstraction
            await _monitor.SetClipboardTextAsync(plaintext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process WebSocket message");
        }
    }

    private void OnWebSocketDisconnected()
    {
        _logger.LogWarning("WebSocket disconnected - clipboard sync paused until reconnection");
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