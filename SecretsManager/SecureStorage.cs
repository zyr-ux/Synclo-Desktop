using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Synclo.SecretsManager;

public static class SecureStorage
{
    private static readonly ISecureStorage _impl;
    
    static SecureStorage()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _impl = new SecureStorageWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            _impl = new SecureStorageMacOS();
        else
            _impl = new SecureStorageLinux();
    }

    public static Task SaveAsync(string key, string value)
    {
        return _impl.SaveAsync(key, value);
    }

    public static Task<string?> LoadAsync(string key)
    {
        return _impl.LoadAsync(key);
    }

    public static Task DeleteAsync(string key)
    {
        return _impl.DeleteAsync(key);
    }
}