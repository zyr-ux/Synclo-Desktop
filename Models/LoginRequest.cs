namespace Synclo.Models;

public class LoginRequest
{
    public string email { get; set; } = string.Empty;
    public string device_id { get; set; } = string.Empty;
    public string device_name { get; set; } = string.Empty;
    public string auth_key { get; set; } = string.Empty; // NEW - replaces password
    public string? os { get; set; } // NEW
}