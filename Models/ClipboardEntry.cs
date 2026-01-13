using System;

namespace Synclo.Models;

public class ClipboardEntry
{
    public required string id { get; set; }
    public required string ciphertext { get; set; }
    public required string nonce { get; set; }
    public int blob_version { get; set; }
    public DateTime timestamp { get; set; }
    
    public string? plaintext { get; set; }
}

