using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Synclo.Models;

public partial class ClipboardDbModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string Ciphertext { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public int BlobVersion { get; init; }
    public DateTime CreatedAt { get; init; }
    
    public DateTime CreatedAtLocal => CreatedAt.ToLocalTime();
    
    [ObservableProperty]
    private bool _isDeleting;
    
    public ClipboardDbModel CopyWith(
        string? id = null,
        string? content = null,
        string? contentHash = null,
        string? ciphertext = null,
        string? nonce = null,
        int? blobVersion = null,
        DateTime? createdAt = null)
    {
        return new ClipboardDbModel
        {
            Id = id ?? this.Id,
            Content = content ?? this.Content,
            ContentHash = contentHash ?? this.ContentHash,
            Ciphertext = ciphertext ?? this.Ciphertext,
            Nonce = nonce ?? this.Nonce,
            BlobVersion = blobVersion ?? this.BlobVersion,
            CreatedAt = createdAt ?? this.CreatedAt
        };
    }
    
    public string PreviewText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Content))
                return string.Empty;

            Span<char> buffer = stackalloc char[Content.Length];
            int len = 0;
            bool lastWasSpace = false;

            foreach (var ch in Content)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (lastWasSpace)
                        continue;

                    buffer[len++] = ' ';
                    lastWasSpace = true;
                }
                else
                {
                    buffer[len++] = ch;
                    lastWasSpace = false;
                }
            }

            // Trim trailing space
            if (len > 0 && buffer[len - 1] == ' ')
                len--;

            return new string(buffer[..len]);
        }
    }
}
