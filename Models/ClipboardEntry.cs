using System;

namespace Synclo.Models;

/// <summary>
/// Represents an encrypted clipboard entry.
/// </summary>
public class ClipboardEntry
{
    public string id { get; set; }
    public string ciphertext { get; set; } // Base64 encoded
    public string nonce { get; set; } // Base64 encoded
    public int blob_version { get; set; }
    public DateTime timestamp { get; set; }
    
    // Client-side only (not in API response)
    public string? plaintext { get; set; } // Decrypted locally
}

