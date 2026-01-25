using System.Threading.Tasks;

namespace Synclo.Services.SecretsManager;

public interface ISecureStorage
{
    Task SaveAsync(string key, string value);
    Task<string?> LoadAsync(string key);
    Task DeleteAsync(string key);
}