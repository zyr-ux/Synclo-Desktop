using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Synclo.Services.Utilities;

namespace Synclo.Services.ClipboardMonitor;

public sealed class ClipboardMonitorMacOS(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorMacOS> logger,
    IUtils utils)
    : IClipboardMonitor, IDisposable
{
    private readonly IClipboardProvider _clipboardProvider = clipboardProvider ?? throw new ArgumentNullException(nameof(clipboardProvider));
    private readonly ILogger<ClipboardMonitorMacOS> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IUtils _utils = utils ?? throw new ArgumentNullException(nameof(utils));

    private readonly object _startLock = new();
    private readonly object _lock = new();

    private Thread? _monitorThread;

    private volatile bool _disposed;
    private volatile bool _stopping;
    private int _generation;

    private nint _lastChangeCount;
    private string? _lastClipboardHash;
    private TaskCompletionSource? _initTcs;
    private volatile bool _startedSuccessfully;

    public event Action<string>? OnClipboardChanged;
    public event Action<Exception?>? OnMonitorStopped;

    public bool IsRunning => _monitorThread is { IsAlive: true } && _startedSuccessfully;

    static ClipboardMonitorMacOS()
    {
        dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_NOW);
        InitializeObjCHandles();
    }

    public Task StartAsync()
    {
        lock (_startLock)
        {
            if (_monitorThread is { IsAlive: true })
                return _initTcs?.Task ?? Task.CompletedTask;

            _initTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = false;
            _startedSuccessfully = false;

            var myGeneration = Interlocked.Increment(ref _generation);

            _monitorThread = new Thread(() => MonitorLoop(myGeneration))
            {
                IsBackground = true,
                Name = "SyncloClipboardMonitorMacOS"
            };

            _monitorThread.Start();
            return _initTcs.Task;
        }
    }

    public Task StopAsync()
    {
        _stopping = true;
        Interlocked.Increment(ref _generation);
        return Task.CompletedTask;
    }

    public async Task SetClipboardTextAsync(string text)
    {
        try
        {
            // Fix: Perform I/O first, then update protected fields atomically
            await _clipboardProvider.SetTextAsync(text);
            var hash = _utils.ComputeHash(text);
            var changeCount = NSPasteboardChangeCount();

            // Only lock briefly to update both fields atomically
            lock (_lock)
            {
                _lastClipboardHash = hash;
                _lastChangeCount = changeCount;
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
        Exception? fatalError = null;

        try
        {
            lock (_lock)
            {
                _lastChangeCount = NSPasteboardChangeCount();
            }

            _startedSuccessfully = true;
            _initTcs?.TrySetResult();
            _initTcs = null;

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

                HandleClipboardUpdateSync(myGeneration);
            }
        }
        catch (Exception ex)
        {
            fatalError = ex;
            _logger.LogError(ex, "macOS clipboard monitor loop failed");

            if (_initTcs != null)
            {
                _initTcs.TrySetException(ex);
                _initTcs = null;
            }
        }
        finally
        {
            _startedSuccessfully = false;

            if (!_stopping && !_disposed)
                OnMonitorStopped?.Invoke(fatalError);

            lock (_startLock)
            {
                _monitorThread = null;
            }
        }
    }

    private void HandleClipboardUpdateSync(int myGeneration)
    {
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (_disposed || myGeneration != Volatile.Read(ref _generation))
                    return;

                var before = NSPasteboardChangeCount();

                var text = GetClipboardTextSync();
                if (string.IsNullOrEmpty(text))
                    return;

                var after = NSPasteboardChangeCount();
                if (before != after)
                {
                    lock (_lock)
                    {
                        _lastChangeCount = after;
                    }

                    continue;
                }

                var hash = _utils.ComputeHash(text);

                lock (_lock)
                {
                    if (hash == _lastClipboardHash)
                        return;

                    _lastClipboardHash = hash;
                }

                var captured = text;
                Task.Run(() => OnClipboardChanged?.Invoke(captured));
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clipboard update handling failed");
        }
    }

    private string? GetClipboardTextSync()
    {
        if (_clipboardProvider is ISynchronousClipboardProvider synchronousClipboardProvider)
            return synchronousClipboardProvider.GetText();

        return _clipboardProvider.GetTextAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        Interlocked.Increment(ref _generation);

        try
        {
            if (_monitorThread is { IsAlive: true })
                _monitorThread.Join(millisecondsTimeout: 1000);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop macOS clipboard monitor thread");
        }
        finally
        {
            _monitorThread = null;
        }
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
