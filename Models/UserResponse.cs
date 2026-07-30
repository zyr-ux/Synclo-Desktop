namespace Synclo.Models;

public sealed class UserResponse
{
    public string user_id { get; set; } = string.Empty;
    public string email { get; set; } = string.Empty;
    public string? username { get; set; }
    public int kdf_version { get; set; }
}
