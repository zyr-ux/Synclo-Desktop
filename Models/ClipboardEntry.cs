using System;

namespace Synclo.Models;

public class ClipboardEntry
{
    public required string id { get; set; }
    public string? ciphertext { get; set; }  // Nullable for tombstones
    public string? nonce { get; set; }       // Nullable for tombstones
    public int blob_version { get; set; }
    public DateTime timestamp { get; set; }
    
    public string? plaintext { get; set; }
    
    public bool is_deleted { get; set; } = false;
    public DateTime? deleted_at { get; set; }
    
    // New field for sync logic
    public DateTime updated_at { get; set; }
    
    public bool is_pinned { get; set; } = false;
}


