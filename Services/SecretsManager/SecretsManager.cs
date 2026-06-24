using System;
using System.Threading.Tasks;
namespace Synclo.Services.SecretsManager;

public interface ISecretsManager
{
    Task<string?> GetAccessTokenAsync();
    Task SaveAccessTokenAsync(string token);
    Task DeleteAccessTokenAsync();

    Task<string?> GetRefreshTokenAsync();
    Task SaveRefreshTokenAsync(string token);
    Task DeleteRefreshTokenAsync();

    Task<string?> GetUserEmailAsync();
    Task SaveUserEmailAsync(string email);
    Task DeleteUserEmailAsync();

    Task<string?> GetMasterKeyAsync();
    Task SaveMasterKeyAsync(string masterKeyBase64);
    Task DeleteMasterKeyAsync();

    Task<string?> GetSaltAsync();
    Task SaveSaltAsync(string salt);
    Task DeleteSaltAsync();

    Task<int?> GetKdfVersionAsync();
    Task SaveKdfVersionAsync(int version);
    Task DeleteKdfVersionAsync();

    Task<string?> GetServerUrlAsync();
    Task SaveServerUrlAsync(string url);
    Task DeleteServerUrlAsync();

    Task ClearAllSecretsAsync();
}

public sealed class SecretsManager : ISecretsManager
{
    private const string Prefix = "com.synclo.app";

    private const string AccessToken      = $"{Prefix}.auth.access_token";
    private const string RefreshToken     = $"{Prefix}.auth.refresh_token";
    private const string UserEmail        = $"{Prefix}.user.email";
    private const string MasterKey        = $"{Prefix}.crypto.master_key";
    private const string Salt             = $"{Prefix}.crypto.salt";
    private const string KdfVersion       = $"{Prefix}.crypto.kdf_version";
    private const string ServerUrl        = $"{Prefix}.config.server_url";

    private readonly ISecureStorage _secureStorage;

    public SecretsManager(ISecureStorage secureStorage)
    {
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
    }

    public Task<string?> GetAccessTokenAsync() => _secureStorage.LoadAsync(AccessToken);
    public Task SaveAccessTokenAsync(string token) => _secureStorage.SaveAsync(AccessToken, token);
    public Task DeleteAccessTokenAsync() => _secureStorage.DeleteAsync(AccessToken);

    public Task<string?> GetRefreshTokenAsync() => _secureStorage.LoadAsync(RefreshToken);
    public Task SaveRefreshTokenAsync(string token) => _secureStorage.SaveAsync(RefreshToken, token);
    public Task DeleteRefreshTokenAsync() => _secureStorage.DeleteAsync(RefreshToken);

    public Task<string?> GetUserEmailAsync() => _secureStorage.LoadAsync(UserEmail);
    public Task SaveUserEmailAsync(string email) => _secureStorage.SaveAsync(UserEmail, email);
    public Task DeleteUserEmailAsync() => _secureStorage.DeleteAsync(UserEmail);

    public Task<string?> GetMasterKeyAsync() => _secureStorage.LoadAsync(MasterKey);
    public Task SaveMasterKeyAsync(string masterKeyBase64) => _secureStorage.SaveAsync(MasterKey, masterKeyBase64);
    public Task DeleteMasterKeyAsync() => _secureStorage.DeleteAsync(MasterKey);

    public Task<string?> GetSaltAsync() => _secureStorage.LoadAsync(Salt);
    public Task SaveSaltAsync(string salt) => _secureStorage.SaveAsync(Salt, salt);
    public Task DeleteSaltAsync() => _secureStorage.DeleteAsync(Salt);

    public async Task<int?> GetKdfVersionAsync()
    {
        var value = await _secureStorage.LoadAsync(KdfVersion);
        return int.TryParse(value, out var version) ? version : null;
    }
    public Task SaveKdfVersionAsync(int version) => _secureStorage.SaveAsync(KdfVersion, version.ToString());
    public Task DeleteKdfVersionAsync() => _secureStorage.DeleteAsync(KdfVersion);

    public Task<string?> GetServerUrlAsync() => _secureStorage.LoadAsync(ServerUrl);
    public Task SaveServerUrlAsync(string url) => _secureStorage.SaveAsync(ServerUrl, url);
    public Task DeleteServerUrlAsync() => _secureStorage.DeleteAsync(ServerUrl);

    public async Task ClearAllSecretsAsync()
    {
        await _secureStorage.DeleteAsync(AccessToken).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(RefreshToken).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(UserEmail).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(MasterKey).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(Salt).ConfigureAwait(false);
        await _secureStorage.DeleteAsync(KdfVersion).ConfigureAwait(false);
    }
}
