using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Synclo.Services.Utilities;

namespace Synclo.Services.ClipboardMonitor;

public sealed class ClipboardMonitorMacOS : IClipboardMonitor, IDisposable
{
    private readonly IClipboardProvider _clipboardProvider;
    private readonly ILogger<ClipboardMonitorMacOS> _logger;
    private readonly IUtils _utils;

    private Thread? _monitorThread;

    private volatile bool _disposed;
    private int _generation;

    private nint _lastChangeCount;
    private string? _lastClipboardHash;

    private readonly object _lock = new();

    public event Action<string>? OnClipboardChanged;

    public bool IsRunning => _monitorThread is { IsAlive: true };

    static ClipboardMonitorMacOS()
    {
        dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_NOW);
        InitializeObjCHandles();
    }

    public ClipboardMonitorMacOS(
        IClipboardProvider clipboardProvider,
        ILogger<ClipboardMonitorMacOS> logger,
        IUtils utils)
    {
        _clipboardProvider = clipboardProvider ?? throw new ArgumentNullException(nameof(clipboardProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _utils = utils ?? throw new ArgumentNullException(nameof(utils));
    }

    public Task StartAsync()
    {
        var myGeneration = Interlocked.Increment(ref _generation);
        _disposed = false;

        _monitorThread = new Thread(() => MonitorLoop(myGeneration))
        {
            IsBackground = true,
            Name = "SyncloClipboardMonitorMacOS"
        };

        _monitorThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _disposed = true;
        Interlocked.Increment(ref _generation);
        _monitorThread = null;
        return Task.CompletedTask;
    }

    public async Task SetClipboardTextAsync(string text)
    {
        try
        {
            var hash = _utils.ComputeHash(text);

            lock (_lock)
            {
                _lastClipboardHash = hash;
            }

            await _clipboardProvider.SetTextAsync(text);

            lock (_lock)
            {
                _lastChangeCount = NSPasteboardChangeCount();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set clipboard text");
            throw;
        }
    }

    private void MonitorLoop(int myGeneration)
    {
        try
        {
            lock (_lock)
            {
                _lastChangeCount = NSPasteboardChangeCount();
            }

            while (true)
            {
                if (_disposed || myGeneration != Volatile.Read(ref _generation))
                    return;

                Thread.Sleep(200);

                if (_disposed || myGeneration != Volatile.Read(ref _generation))
                    return;

                var current = NSPasteboardChangeCount();

                lock (_lock)
                {
                    if (current == _lastChangeCount)
                        continue;

                    _lastChangeCount = current;
                }

                _ = HandleClipboardUpdateAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "macOS clipboard monitor loop failed");
        }
    }

    private async Task HandleClipboardUpdateAsync()
    {
        try
        {
            var text = await _clipboardProvider.GetTextAsync();
            if (string.IsNullOrEmpty(text))
                return;

            var hash = _utils.ComputeHash(text);

            lock (_lock)
            {
                if (hash == _lastClipboardHash)
                    return;

                _lastClipboardHash = hash;
            }

            OnClipboardChanged?.Invoke(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clipboard update handling failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        StopAsync().Wait();
    }

    #region Objective-C bridge (cached)

    private const string ObjCLibrary = "/usr/lib/libobjc.dylib";
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const int RTLD_NOW = 2;

    private static IntPtr _nsPasteboardClass;
    private static IntPtr _selGeneralPasteboard;
    private static IntPtr _selChangeCount;

    [DllImport(SystemLibrary)]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport(ObjCLibrary)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLibrary)]
    private static extern IntPtr sel_registerName(string selectorName);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nint(IntPtr receiver, IntPtr selector);

    private static void InitializeObjCHandles()
    {
        _nsPasteboardClass = objc_getClass("NSPasteboard");
        _selGeneralPasteboard = sel_registerName("generalPasteboard");
        _selChangeCount = sel_registerName("changeCount");
    }

    private static nint NSPasteboardChangeCount()
    {
        if (_nsPasteboardClass == IntPtr.Zero)
            return 0;

        var pasteboard = objc_msgSend_IntPtr(_nsPasteboardClass, _selGeneralPasteboard);
        if (pasteboard == IntPtr.Zero)
            return 0;

        return objc_msgSend_nint(pasteboard, _selChangeCount);
    }

    #endregion
}