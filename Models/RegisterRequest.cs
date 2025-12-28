namespace Synclo.Models;

public class RegisterRequest
{
    public string email { get; set; }
    public string device_id { get; set; }
    public string device_name { get; set; }
    public string auth_key { get; set; } // NEW - replaces password
    public string encrypted_master_key { get; set; } // NEW
    public string salt { get; set; } // NEW
    public int kdf_version { get; set; } // NEW
}