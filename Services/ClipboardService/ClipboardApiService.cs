using System;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;
using Microsoft.Extensions.Logging;

namespace Synclo.Services.ClipboardService;

public interface IClipboardApiService
{
    Task<string> SyncClipboardAsync(string content, int blobVersion = 1);
    Task<string> GetLatestClipboardAsync();
    Task<ClipboardHistoryResponse> GetClipboardHistoryAsync(int page = 1, int pageSize = 20);
    Task<ClipboardDeleteResponse> DeleteClipboardAsync(string clipboardId);
    T Deserialize<T>(string json);
}

public class ClipboardApiService : IClipboardApiService
{
    private readonly ApiService _api;
    private readonly CryptographyService _cryptographyService;
    private readonly ISecureStorage _secureStorage;
    private readonly ILogger<ClipboardApiService> _logger;
    private readonly ApiConfig _config;

    public ClipboardApiService(
        ApiService api, 
        CryptographyService cryptographyService, 
        ISecureStorage secureStorage,
        ILogger<ClipboardApiService> logger,
        ApiConfig config)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _cryptographyService = cryptographyService ?? throw new ArgumentNullException(nameof(cryptographyService));
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<string> SyncClipboardAsync(string content, int blobVersion = 1)
    {
        try
        {
            if (string.IsNullOrEmpty(content))
                throw new ArgumentException("Content cannot be null or empty", nameof(content));
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.ApiTimeoutSeconds));
            var masterKeyBase64 = await _secureStorage.LoadAsync(CryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
                throw new InvalidOperationException("Master key not found. User must be logged in.");
            
            var masterKey = CryptographyService.FromBase64Static(masterKeyBase64);
            var (ciphertext, nonce) = _cryptographyService.EncryptClipboard(content, masterKey);
            
            var request = new ClipboardSyncRequest
            {
                ciphertext = CryptographyService.ToBase64Static(ciphertext),
                nonce = CryptographyService.ToBase64Static(nonce),
                blob_version = blobVersion
            };
            
            var response = await _api.PostAsync("api/clipboard", request, cts.Token);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("Server returned empty response");
            
            ClipboardSyncResponse? syncResponse;
            try
            {
                syncResponse = _api.Deserialize<ClipboardSyncResponse>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize server response. JSON: {json}", ex);
            }
            
            if (syncResponse == null)
                throw new InvalidOperationException("Server returned null response");
            
            return syncResponse.id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync clipboard");
            throw;
        }
    }

    public async Task<string> GetLatestClipboardAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.ApiTimeoutSeconds));
            var masterKeyBase64 = await _secureStorage.LoadAsync(CryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
                throw new InvalidOperationException("Master key not found. User must be logged in.");
            
            var masterKey = CryptographyService.FromBase64Static(masterKeyBase64);
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.ApiTimeoutSeconds));
            var masterKeyBase64 = await _secureStorage.LoadAsync(CryptographyService.MasterKey);
            if (string.IsNullOrEmpty(masterKeyBase64))
                throw new InvalidOperationException("Master key not found. User must be logged in.");
            
            var masterKey = CryptographyService.FromBase64Static(masterKeyBase64);
            var response = await _api.GetAsync($"/api/clipboard/all?page={page}&limit={pageSize}", cts.Token);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var historyResponse = _api.Deserialize<ClipboardHistoryResponse>(json);
            
            foreach (var entry in historyResponse.history)
            {
                entry.plaintext = DecryptClipboardEntry(entry, masterKey);
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
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.ApiTimeoutSeconds));
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

    private string DecryptClipboardEntry(ClipboardEntry entry, byte[] masterKey)
    {
        if (entry == null)
            throw new ArgumentNullException(nameof(entry));
        if (masterKey == null)
            throw new ArgumentNullException(nameof(masterKey));
        
        var ciphertext = CryptographyService.FromBase64Static(entry.ciphertext ?? string.Empty);
        var nonce = CryptographyService.FromBase64Static(entry.nonce ?? string.Empty);
        return _cryptographyService.DecryptClipboard(ciphertext, nonce, masterKey);
    }

    public T Deserialize<T>(string json)
    {
        return _api.Deserialize<T>(json);
    }
}

public class ApiConfig
{
    public int ApiTimeoutSeconds { get; set; } = 30;
}