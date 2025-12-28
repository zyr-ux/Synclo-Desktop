namespace Synclo.Models;

/// <summary>
/// Request body for syncing encrypted clipboard content.
/// </summary>
public class ClipboardSyncRequest
{
    public string ciphertext { get; set; } // Base64 encoded
    public string nonce { get; set; } // Base64 encoded
    public int blob_version { get; set; } = 1;
}

