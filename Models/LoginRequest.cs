namespace Synclo.Models;

public class LoginRequest
{
    public string email { get; set; }
    public string device_id { get; set; }
    public string device_name { get; set; }
    public string auth_key { get; set; } // NEW - replaces password
}