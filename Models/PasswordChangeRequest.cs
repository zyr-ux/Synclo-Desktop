namespace Synclo.Models;

public class PasswordChangeRequest
{
    public required string old_auth_key { get; set; }
    public required string new_auth_key { get; set; }
    public required string new_encrypted_master_key { get; set; }
    public required string new_salt { get; set; }
    public int new_kdf_version { get; set; } = 1;
}
