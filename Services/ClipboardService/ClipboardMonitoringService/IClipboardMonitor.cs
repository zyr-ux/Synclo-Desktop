using System;
using System.Threading.Tasks;

namespace Synclo.Services.ClipboardService;


public interface IClipboardMonitor
{
    event Action<string>? OnClipboardChanged;
    
    Task StartAsync();
    
    Task StopAsync();
    
    bool IsRunning { get; }
    
    Task SetClipboardTextAsync(string text);
}
