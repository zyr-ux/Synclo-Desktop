namespace Synclo.Models;

/// <summary>
/// Response from /auth/salt endpoint containing KDF parameters.
/// </summary>
public class SaltResponse
{
    public string salt { get; set; }
    public int kdf_version { get; set; }
}

