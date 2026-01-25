using System;

namespace Synclo.Models;

public class WebSocketAckResponse
{
    public required string type { get; set; }
    public required string id { get; set; }
}
