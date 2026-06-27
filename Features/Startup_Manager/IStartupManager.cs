using System.Threading.Tasks;

namespace Synclo.Features.Startup_Manager;

public interface IStartupManager
{
    Task<bool> IsEnabledAsync();
    Task EnableAsync();
    Task DisableAsync();
    bool IsSupported { get; }
}
