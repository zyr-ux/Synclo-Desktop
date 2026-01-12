using System;

namespace Synclo.Models;

/// <summary>
/// SQLite database model for clipboard entries.
/// Represents the local storage structure with additional fields for echo suppression and soft deletion.
/// </summary>
public class ClipboardDbModel
{
    /// <summary>
    /// Server-assigned unique identifier
    /// </summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>
    /// Decrypted plaintext content
    /// </summary>
    public string Content { get; set; } = string.Empty;
    
    /// <summary>
    /// SHA256 hash of content for echo suppression
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;
    
    /// <summary>
    /// Base64 encoded encrypted content
    /// </summary>
    public string Ciphertext { get; set; } = string.Empty;
    
    /// <summary>
    /// Base64 encoded encryption nonce
    /// </summary>
    public string Nonce { get; set; } = string.Empty;
    
    /// <summary>
    /// Encryption blob version
    /// </summary>
    public int BlobVersion { get; set; }
    
    /// <summary>
    /// Runtime soft delete flag (entry deleted on server but kept locally until shutdown)
    /// </summary>
    public bool IsRemoteDeleted { get; set; }
    
    /// <summary>
    /// Timestamp when entry was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Timestamp of last successful sync (null if never synced)
    /// </summary>
    public DateTime? SyncedAt { get; set; }
}
