using System;

namespace Synclo.Models;

public class WebSocketDeleteResponse
{
    public string type { get; set; } = string.Empty;
    public string id { get; set; } = string.Empty;
    public bool is_deleted { get; set; }
    public DateTime? deleted_at { get; set; }
}
