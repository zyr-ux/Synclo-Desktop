namespace Synclo.Models;

public class AuthResponse
{
    public string access_token { get; set; }
    public string refresh_token { get; set; }
    public string token_type { get; set; }
    
    // NEW - Only present in login response
    public string? encrypted_master_key { get; set; }
    public string? salt { get; set; }
    public int? kdf_version { get; set; }
}