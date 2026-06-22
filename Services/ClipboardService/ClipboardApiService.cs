using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Synclo.Models;
using Synclo.Services.API;
using Synclo.Services.SecretsManager;
using Synclo.Services.Utilities;

namespace Synclo.Services.ClipboardService;

public interface IClipboardApiService
{
    Task<string> GetLatestClipboardAsync();
    Task<ClipboardSyncResponse> GetClipboardSyncAsync(DateTime? since = null, int limit = 1000, long offset = 0);
    Task<ClipboardDeleteResponse> DeleteClipboardAsync(string clipboardId);
    Task<ClipboardDeleteResponse> ClearClipboardHistoryAsync();
    T Deserialize<T>(string json);
}

public class ClipboardApiService(
    IApiService api,
    ICryptographyService cryptographyService,
    ISecureStorage secureStorage,
    ILogger<ClipboardApiService> logger)
    : IClipboardApiService
{
    private const int ApiTimeoutSeconds = 30;
    private readonly IApiService _api = api ?? throw new ArgumentNullException(nameof(api));

    private readonly ICryptographyService _cryptographyService =
        cryptographyService ?? throw new ArgumentNullException(nameof(cryptographyService));

    private readonly ILogger<ClipboardApiService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ISecureStorage _secureStorage =
        secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));


    public async Task<string> GetLatestClipboardAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ApiTimeoutSeconds));
            var masterKeyBase64 = await _secureStorage.LoadAsync(_cryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
                throw new InvalidOperationException("Master key not found. User must be logged in.");

            var masterKey = _cryptographyService.FromBase64(masterKeyBase64);
            var response = await _api.GetAsync("/api/clipboard", cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var entry = _api.Deserialize<ClipboardEntry>(json);
            return DecryptClipboardEntry(entry, masterKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get latest clipboard");
            throw;
        }
    }

    public async Task<ClipboardSyncResponse> GetClipboardSyncAsync(DateTime? since = null, int limit = 1000, long offset = 0)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ApiTimeoutSeconds));
            var masterKeyBase64 = await _secureStorage.LoadAsync(_cryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
                throw new InvalidOperationException("Master key not found. User must be logged in.");

            var masterKey = _cryptographyService.FromBase64(masterKeyBase64);
            
            var query = $"/api/clipboard/sync?limit={limit}&offset={offset}";
            if (since.HasValue)
            {
                // Format as ISO 8601
                query += $"&since={since.Value.ToString("O")}";
            }

            var response = await _api.GetAsync(query, cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var syncResponse = _api.Deserialize<ClipboardSyncResponse>(json);

            if (syncResponse?.entries != null)
            {
                foreach (var entry in syncResponse.entries)
                {
                    if (entry == null) continue;
                    try
                    {
                        entry.plaintext = DecryptClipboardEntry(entry, masterKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to decrypt entry {entry.id ?? "unknown"}, skipping");
                        entry.plaintext = null; // Mark as failed
                    }
                }
            }

            return syncResponse ?? new ClipboardSyncResponse();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get clipboard sync data");
            throw;
        }
    }

    public async Task<ClipboardDeleteResponse> DeleteClipboardAsync(string clipboardId)
    {
        try
        {
            if (string.IsNullOrEmpty(clipboardId))
                throw new ArgumentException("Clipboard ID cannot be null or empty", nameof(clipboardId));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ApiTimeoutSeconds));
            var response = await _api.DeleteAsync($"/api/clipboard/{clipboardId}", cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var deleteResponse = _api.Deserialize<ClipboardDeleteResponse>(json);

            if (deleteResponse == null)
                throw new InvalidOperationException("Server returned null response");

            return deleteResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete clipboard entry: {clipboardId}");
            throw;
        }
    }

    public async Task<ClipboardDeleteResponse> ClearClipboardHistoryAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ApiTimeoutSeconds));
            var response = await _api.DeleteAsync("/api/clipboard", cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var deleteResponse = _api.Deserialize<ClipboardDeleteResponse>(json);

            if (deleteResponse == null)
                throw new InvalidOperationException("Server returned null response");

            return deleteResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear clipboard history on server");
            throw;
        }
    }

    public T Deserialize<T>(string json)
    {
        return _api.Deserialize<T>(json);
    }

    private string DecryptClipboardEntry(ClipboardEntry entry, byte[] masterKey)
    {
        if (entry == null)
            throw new ArgumentNullException(nameof(entry));
        if (masterKey == null)
            throw new ArgumentNullException(nameof(masterKey));

        var ciphertext = _cryptographyService.FromBase64(entry.ciphertext ?? string.Empty);
        var nonce = _cryptographyService.FromBase64(entry.nonce ?? string.Empty);
        return _cryptographyService.DecryptClipboard(ciphertext, nonce, masterKey);
    }
}