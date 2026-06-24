namespace Synclo.Models;

public static class Constants
{
    private const string Prefix = "com.synclo.app";

    public const string AccessToken      = $"{Prefix}.auth.access_token";
    public const string RefreshToken     = $"{Prefix}.auth.refresh_token";
    public const string UserEmail        = $"{Prefix}.user.email";
    public const string MasterKey        = $"{Prefix}.crypto.master_key";
    public const string Salt             = $"{Prefix}.crypto.salt";
    public const string KdfVersion       = $"{Prefix}.crypto.kdf_version";
    public const string ServerUrl        = $"{Prefix}.config.server_url";
}
