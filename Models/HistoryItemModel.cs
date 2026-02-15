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
    
    public DateTime CreatedAtLocal => CreatedAt.ToLocalTime();
    
    [ObservableProperty]
    private bool _isDeleting;
    
    public HistoryItemModel CopyWith(
        string? id = null,
        string? content = null,
        string? contentHash = null,
        string? ciphertext = null,
        string? nonce = null,
        int? blobVersion = null,
        DateTime? createdAt = null,
        bool? isSynced = null,
        bool? isDeleted = null)
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
            IsDeleted = isDeleted ?? this.IsDeleted
        };
    }
    
    public enum ClipboardItemType
    {
        Text,
        Link,
        Image,
        Code
    }

    public Material.Icons.MaterialIconKind IconKind => Type switch
    {
        ClipboardItemType.Text => Material.Icons.MaterialIconKind.FormatAlignLeft,
        ClipboardItemType.Link => Material.Icons.MaterialIconKind.Link,
        ClipboardItemType.Image => Material.Icons.MaterialIconKind.Image,
        ClipboardItemType.Code => Material.Icons.MaterialIconKind.CodeBraces, // or CodeTags
        _ => Material.Icons.MaterialIconKind.Help
    };

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

        // Specific to detailed code blocks
        var lines = text.Split('\n');
        if (lines.Length > 2)
        {
            // Indentation check
            if (lines.Any(l => l.StartsWith("    ") || l.StartsWith("\t"))) score += 1;
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
