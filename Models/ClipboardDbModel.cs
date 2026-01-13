using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Synclo.Models;

/// <summary>
/// Immutable model for clipboard entries stored in SQLite.
/// All properties use init-only setters to prevent thread safety issues.
/// Use CopyWith() method to create modified copies.
/// </summary>
public partial class ClipboardDbModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string Ciphertext { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public int BlobVersion { get; init; }
    public bool IsRemoteDeleted { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? SyncedAt { get; init; }
    
    public DateTime CreatedAtLocal => CreatedAt.ToLocalTime();
    
    /// <summary>
    /// UI-only state for delete operation (not persisted to DB).
    /// This is the only mutable property as it's UI-specific.
    /// </summary>
    [ObservableProperty]
    private bool _isDeleting;
    
    /// <summary>
    /// Creates a copy of this instance with specified properties modified.
    /// </summary>
    public ClipboardDbModel CopyWith(
        string? id = null,
        string? content = null,
        string? contentHash = null,
        string? ciphertext = null,
        string? nonce = null,
        int? blobVersion = null,
        bool? isRemoteDeleted = null,
        DateTime? createdAt = null,
        DateTime? syncedAt = null)
    {
        return new ClipboardDbModel
        {
            Id = id ?? this.Id,
            Content = content ?? this.Content,
            ContentHash = contentHash ?? this.ContentHash,
            Ciphertext = ciphertext ?? this.Ciphertext,
            Nonce = nonce ?? this.Nonce,
            BlobVersion = blobVersion ?? this.BlobVersion,
            IsRemoteDeleted = isRemoteDeleted ?? this.IsRemoteDeleted,
            CreatedAt = createdAt ?? this.CreatedAt,
            SyncedAt = syncedAt ?? this.SyncedAt
        };
    }
}
