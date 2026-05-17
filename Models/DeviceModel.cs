namespace Synclo.Models;

public class DeviceModel
{
    public string device_id { get; set; } = string.Empty;
    public string device_name { get; set; } = string.Empty;
    public string? os { get; set; }
    public bool IsThisDevice { get; set; }
}