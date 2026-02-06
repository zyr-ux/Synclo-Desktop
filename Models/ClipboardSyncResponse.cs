using System.Collections.Generic;

namespace Synclo.Models;

public class ClipboardSyncResponse
{
    public List<ClipboardEntry> entries { get; set; } = new();
    public long next_offset { get; set; }
    public bool has_more { get; set; }
    public int? total_count { get; set; }
}
