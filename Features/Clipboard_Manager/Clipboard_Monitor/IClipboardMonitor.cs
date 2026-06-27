using System;
using System.Threading.Tasks;

namespace Synclo.Features.Clipboard_Manager.Clipboard_Monitor;


public interface IClipboardMonitor
{
    event Action<string>? OnClipboardChanged;
    
    Task StartAsync();
    
    Task StopAsync();
    
    bool IsRunning { get; }
    
    Task SetClipboardTextAsync(string text);
}
