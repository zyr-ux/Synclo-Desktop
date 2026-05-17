namespace Synclo.Models;

public class RegisterRequest
{
    public string email { get; set; } = string.Empty;
    public string device_id { get; set; } = string.Empty;
    public string device_name { get; set; } = string.Empty;
    public string auth_key { get; set; } = string.Empty; // NEW - replaces password
    public string encrypted_master_key { get; set; } = string.Empty; // NEW
    public string salt { get; set; } = string.Empty; // NEW
    public int kdf_version { get; set; } // NEW
    public string? os { get; set; } // NEW
}