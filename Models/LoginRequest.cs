namespace Synclo.Models;

public class LoginRequest
{
    public string email { get; set; }
    public string password { get; set; }
    public string device_id { get; set; }
    public string device_name { get; set; }
}