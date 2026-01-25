using System.Threading.Tasks;

namespace Synclo.Services.Startup;

public interface IStartupManager
{
    Task<bool> IsEnabledAsync();
    Task EnableAsync();
    Task DisableAsync();
    bool IsSupported { get; }
}
