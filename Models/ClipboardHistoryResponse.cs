using System.Collections.Generic;

namespace Synclo.Models;

/// <summary>
/// Response from /clipboard/all endpoint containing paginated history.
/// </summary>
public class ClipboardHistoryResponse
{
    public List<ClipboardEntry> history { get; set; }
}

