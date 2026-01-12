using System.Threading.Tasks;

namespace Synclo.Services.Startup;

/// <summary>
/// Interface for managing application autostart on system boot.
/// Platform-specific implementations handle OS-specific autostart mechanisms.
/// </summary>
public interface IStartupManager
{
    /// <summary>
    /// Check if autostart is currently enabled
    /// </summary>
    Task<bool> IsEnabledAsync();
    
    /// <summary>
    /// Enable autostart on system boot
    /// </summary>
    Task EnableAsync();
    
    /// <summary>
    /// Disable autostart on system boot
    /// </summary>
    Task DisableAsync();
    
    /// <summary>
    /// Indicates if autostart is supported on this platform
    /// </summary>
    bool IsSupported { get; }
}
