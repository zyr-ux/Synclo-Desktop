using System;

namespace Synclo.Models;

public class ClipboardSyncRequest
{
    public required string id { get; set; }
    public required string ciphertext { get; set; }
    public required string nonce { get; set; }
    public int blob_version { get; set; } = 1;
    public DateTime timestamp { get; set; }
}

