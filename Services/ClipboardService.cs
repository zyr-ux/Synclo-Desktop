using System;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

public class ClipboardService(APIService api)
{
    private readonly APIService _api = api ?? throw new ArgumentNullException(nameof(api));

    public async Task<ClipboardEntry> SyncClipboardAsync(string content)
    {
        if (string.IsNullOrEmpty(content))
            throw new ArgumentException("Content cannot be null or empty", nameof(content));

        var masterKeyBase64 = await SecureStorage.LoadAsync(CryptographyService.MasterKey);
        if (string.IsNullOrEmpty(masterKeyBase64))
            throw new InvalidOperationException("Master key not found. User must be logged in.");

        var masterKey = CryptographyService.FromBase64Static(masterKeyBase64);

        var (ciphertext, nonce) = _api.CryptographyService.EncryptClipboard(content, masterKey);

        var request = new ClipboardSyncRequest
        {
            ciphertext = CryptographyService.ToBase64Static(ciphertext),
            nonce = CryptographyService.ToBase64Static(nonce),
            blob_version = 1
        };

        var response = await _api.PostAsync("api/clipboard", request);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync();
        var entry = _api.Deserialize<ClipboardEntry>(json);
        
        return entry;
    }

    public async Task<string> GetLatestClipboardAsync()
    {
        var masterKeyBase64 = await SecureStorage.LoadAsync(CryptographyService.MasterKey);
        if (string.IsNullOrEmpty(masterKeyBase64))
            throw new InvalidOperationException("Master key not found. User must be logged in.");

        var masterKey = CryptographyService.FromBase64Static(masterKeyBase64);

        var response = await _api.GetAsync("/api/clipboard");
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync();
        var entry = _api.Deserialize<ClipboardEntry>(json);
        
        return DecryptClipboardEntry(entry, masterKey);
    }

    public async Task<ClipboardHistoryResponse> GetClipboardHistoryAsync(int page = 1, int pageSize = 20)
    {
        var masterKeyBase64 = await SecureStorage.LoadAsync(CryptographyService.MasterKey);
        if (string.IsNullOrEmpty(masterKeyBase64))
            throw new InvalidOperationException("Master key not found. User must be logged in.");

        var masterKey = CryptographyService.FromBase64Static(masterKeyBase64);

        var response = await _api.GetAsync($"/api/clipboard/all?page={page}&page_size={pageSize}");
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync();
        var historyResponse = _api.Deserialize<ClipboardHistoryResponse>(json);
        
        foreach (var entry in historyResponse.history)
        {
            entry.plaintext = DecryptClipboardEntry(entry, masterKey);
        }
        
        return historyResponse;
    }

    private string DecryptClipboardEntry(ClipboardEntry entry, byte[] masterKey)
    {
        if (entry == null)
            throw new ArgumentNullException(nameof(entry));
        if (masterKey == null)
            throw new ArgumentNullException(nameof(masterKey));

        var ciphertext = CryptographyService.FromBase64Static(entry.ciphertext);
        var nonce = CryptographyService.FromBase64Static(entry.nonce);

        return _api.CryptographyService.DecryptClipboard(ciphertext, nonce, masterKey);
    }
}