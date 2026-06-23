using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Synclo.Models;

public partial class HistoryItemModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;

    // Cached properties - Lazily computed to avoid heavy regex/parsing on instantiation
    private string? _previewText;
    public string PreviewText => _previewText ??= ComputePreviewText(Content);

    private ClipboardItemType? _type;
    public ClipboardItemType Type => _type ??= ComputeType(Content);

    public string ContentHash { get; init; } = string.Empty;
    public string Ciphertext { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public int BlobVersion { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsSynced { get; init; } = false;
    public bool IsDeleted { get; init; } = false;
    
    [ObservableProperty]
    private bool _isPinned;

    public DateTime? PinnedAt { get; init; }

    public DateTime CreatedAtLocal => CreatedAt.ToLocalTime();
    
    [ObservableProperty]
    private bool _isDeleting;
    
    [ObservableProperty]
    private bool _isBeingCleared;
    
    public HistoryItemModel CopyWith(
        string? id = null,
        string? content = null,
        string? contentHash = null,
        string? ciphertext = null,
        string? nonce = null,
        int? blobVersion = null,
        DateTime? createdAt = null,
        bool? isSynced = null,
        bool? isDeleted = null,
        bool? isPinned = null,
        DateTime? pinnedAt = null,
        bool clearPinnedAt = false)
    {
        return new HistoryItemModel
        {
            Id = id ?? this.Id,
            Content = content ?? this.Content,
            ContentHash = contentHash ?? this.ContentHash,
            Ciphertext = ciphertext ?? this.Ciphertext,
            Nonce = nonce ?? this.Nonce,
            BlobVersion = blobVersion ?? this.BlobVersion,
            CreatedAt = createdAt ?? this.CreatedAt,
            IsSynced = isSynced ?? this.IsSynced,
            IsDeleted = isDeleted ?? this.IsDeleted,
            IsPinned = isPinned ?? this.IsPinned,
            PinnedAt = clearPinnedAt ? null : (pinnedAt ?? this.PinnedAt)
        };
    }
    
    public enum ClipboardItemType
    {
        Text,
        Link,
        Image,
        Code
    }

    private static ClipboardItemType ComputeType(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ClipboardItemType.Text;

        var trimmed = content.Trim();

        // Check for Link
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && 
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return ClipboardItemType.Link;
        }
        
        // Check for Image (File path)
        if (IsImageFile(trimmed))
        {
            return ClipboardItemType.Image;
        }

        // Check for Code (Heuristic)
        if (IsCode(trimmed))
        {
            return ClipboardItemType.Code;
        }

        return ClipboardItemType.Text;
    }

    private static bool IsImageFile(string text)
    {
        if (text.Length > 260) return false; // Max path length check
        if (text.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return false;

        var extension = System.IO.Path.GetExtension(text).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg";
    }

    private static bool IsCode(string text)
    {
        if (text.Length < 20) return false; // Too short to be meaningful code

        int score = 0;
        
        // Common code indicators
        if (text.Contains("{") && text.Contains("}")) score += 2;
        if (text.Contains(";") && text.Contains("\n")) score += 1;
        if (text.Contains("public ") || text.Contains("private ") || text.Contains("protected ")) score += 2;
        if (text.Contains("function ") || text.Contains("def ") || text.Contains("void ")) score += 2;
        if (text.Contains("import ") || text.Contains("using ") || text.Contains("#include ")) score += 2;
        if (text.Contains("return ")) score += 1;
        if (text.Contains("if (") || text.Contains("for (") || text.Contains("while (")) score += 1; // C-style
        if (text.Contains("=>")) score += 1; // Arrows

        // Specific to detailed code blocks - checking indentation without splitting
        // We look for lines starting with spaces or tabs
        var span = text.AsSpan();
        int lineStart = 0;
        int indentationMatches = 0;
        
        while (lineStart < span.Length)
        {
            var lineEnd = span.Slice(lineStart).IndexOf('\n');
            var lineLength = lineEnd == -1 ? span.Length - lineStart : lineEnd;
            var line = span.Slice(lineStart, lineLength);
            
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
               indentationMatches++;
               if (indentationMatches >= 2)
               {
                   score += 1;
                   break;
               }
            }
            
            if (lineEnd == -1) break;
            lineStart += lineEnd + 1;
        }

        return score >= 3;
    }

    private static string ComputePreviewText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        Span<char> buffer = stackalloc char[content.Length];
        int len = 0;
        bool lastWasSpace = false;

        foreach (var ch in content)
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
