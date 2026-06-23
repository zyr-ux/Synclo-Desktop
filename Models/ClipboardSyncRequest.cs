using System;

namespace Synclo.Models;

public class ClipboardSyncRequest
{
    public required string id { get; set; }
    public string? ciphertext { get; set; }  // Nullable for tombstones
    public string? nonce { get; set; }       // Nullable for tombstones
    public int blob_version { get; set; } = 1;
    public DateTime timestamp { get; set; }
    public bool is_deleted { get; set; } = false;
    public bool is_pinned { get; set; } = false;
}

