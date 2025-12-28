namespace Synclo.Models;

public class DeviceModel
{
    public string device_id { get; set; }
    public string device_name { get; set; }
    public bool IsThisDevice => device_id == App.Settings.Settings.device_id;
}