using System;
using System.Threading.Tasks;

namespace Synclo.Services.ClipboardService;

/// <summary>
/// Platform abstraction for clipboard monitoring.
/// Implementations should detect OS clipboard changes and fire events.
/// </summary>
public interface IClipboardMonitor
{
    /// <summary>
    /// Event fired when clipboard content changes (user-originated)
    /// </summary>
    event Action<string>? OnClipboardChanged;
    
    /// <summary>
    /// Start monitoring clipboard changes
    /// </summary>
    Task StartAsync();
    
    /// <summary>
    /// Stop monitoring clipboard changes
    /// </summary>
    Task StopAsync();
    
    /// <summary>
    /// Indicates whether monitoring is currently active
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Sets the clipboard content safely on the UI thread.
    /// </summary>
    /// <param name="text">Text to copy to clipboard</param>
    Task SetClipboardTextAsync(string text);
}
