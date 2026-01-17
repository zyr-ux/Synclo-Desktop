using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Synclo.Models;
using Synclo.Services.SecretsManager;

namespace Synclo.Services.ClipboardService;

public interface IClipboardApiService
{
    Task<string> GetLatestClipboardAsync();
    Task<ClipboardHistoryResponse> GetClipboardHistoryAsync(int page = 1, int pageSize = 20);
    Task<ClipboardDeleteResponse> DeleteClipboardAsync(string clipboardId);
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

    public async Task<ClipboardHistoryResponse> GetClipboardHistoryAsync(int page = 1, int pageSize = 20)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ApiTimeoutSeconds));
            var masterKeyBase64 = await _secureStorage.LoadAsync(_cryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
                throw new InvalidOperationException("Master key not found. User must be logged in.");

            var masterKey = _cryptographyService.FromBase64(masterKeyBase64);
            var response = await _api.GetAsync($"/api/clipboard/all?page={page}&limit={pageSize}", cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var historyResponse = _api.Deserialize<ClipboardHistoryResponse>(json);

            foreach (var entry in historyResponse.history)
            {
                try
                {
                    entry.plaintext = DecryptClipboardEntry(entry, masterKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to decrypt entry {entry?.id ?? "unknown"}, skipping");
                    entry.plaintext = null; // Mark as failed
                }
            }

            return historyResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get clipboard history");
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