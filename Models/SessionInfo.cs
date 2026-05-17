using System;

namespace Synclo.Models;

/// <summary>
/// Represents an active session for a device.
/// </summary>
public class SessionInfo
{
    public string device_id { get; set; } = string.Empty;
    public DateTime expiry { get; set; }
}

