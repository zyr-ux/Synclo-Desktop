namespace Synclo.Models;

/// <summary>
/// Response from /auth/salt endpoint containing KDF parameters.
/// </summary>
public class SaltResponse
{
    public string salt { get; set; } = string.Empty;
    public int kdf_version { get; set; }
}

