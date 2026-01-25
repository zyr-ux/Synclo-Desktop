using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Synclo.Services.Utilities;

namespace Synclo.Services.ClipboardMonitor;

public sealed class ClipboardMonitorWindows : IClipboardMonitor, IDisposable
{
    private readonly IClipboardProvider _clipboardProvider;
    private readonly ILogger<ClipboardMonitorWindows> _logger;
    private readonly IUtils _utils;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly object _startLock = new();

    private Thread? _messageThread;
    private IntPtr _hwnd;
    private volatile bool _disposed;
    private volatile bool _stopping; // New: Tracks intentional shutdown
    private string? _lastClipboardHash;

    private TaskCompletionSource? _initTcs;
    private volatile bool _startedSuccessfully;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate? _wndProcDelegate;

    public event Action<string>? OnClipboardChanged;
    public event Action<Exception?>? OnMonitorStopped;

    public bool IsRunning => _messageThread is { IsAlive: true } && _startedSuccessfully;

    public ClipboardMonitorWindows(
        IClipboardProvider clipboardProvider,
        ILogger<ClipboardMonitorWindows> logger,
        IUtils utils)
    {
        _clipboardProvider = clipboardProvider ?? throw new ArgumentNullException(nameof(clipboardProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _utils = utils ?? throw new ArgumentNullException(nameof(utils));
    }

    public Task StartAsync()
    {
        // Fix 2: Start guard to prevent race conditions on _initTcs
        lock (_startLock)
        {
            if (_messageThread != null)
                return _initTcs?.Task ?? Task.CompletedTask;

            _initTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = false;
            _startedSuccessfully = false;

            _messageThread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "SyncloClipboardMonitorWindows"
            };

            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();
        }

        return _initTcs.Task;
    }

    public Task StopAsync()
    {
        _stopping = true; // Fix 3: Mark as intentional stop

        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        return Task.CompletedTask;
    }

    public async Task SetClipboardTextAsync(string text)
    {
        await _updateLock.WaitAsync();
        try
        {
            await _clipboardProvider.SetTextAsync(text);
            _lastClipboardHash = _utils.ComputeHash(text);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private void MessageLoop()
    {
        Exception? fatalError = null;

        try
        {
            _hwnd = CreateMessageWindow();
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create message window");

            bool hooked = false;
            for (int i = 0; i < 3; i++)
            {
                if (AddClipboardFormatListener(_hwnd))
                {
                    hooked = true;
                    break;
                }
                Thread.Sleep(100);
            }

            if (!hooked)
                throw new InvalidOperationException("Failed to register clipboard listener");

            _startedSuccessfully = true;
            _initTcs?.TrySetResult();
            _initTcs = null;

            MSG msg;
            int ret;
            while ((ret = GetMessage(out msg, IntPtr.Zero, 0, 0)) != 0)
            {
                if (ret == -1)
                {
                    throw new InvalidOperationException("GetMessage failed with error code " + Marshal.GetLastWin32Error());
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            fatalError = ex;
            _logger.LogError(ex, "Clipboard monitor crashed");

            if (_initTcs != null)
            {
                _initTcs.TrySetException(ex);
                _initTcs = null;
            }
        }
        finally
        {
            _startedSuccessfully = false;

            if (_hwnd != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_hwnd);
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            // Fix 3: Only fire stopped event if not intentional
            if (!_stopping && !_disposed)
                OnMonitorStopped?.Invoke(fatalError);
                
            _messageThread = null;
        }
    }

    private IntPtr CreateMessageWindow()
    {
        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            lpszClassName = "SyncloClipboardListener",
            hInstance = GetModuleHandle(null)
        };

        var atom = RegisterClassEx(ref wc);
        if (atom == 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_CLASS_ALREADY_EXISTS)
                return IntPtr.Zero;
        }

        return CreateWindowEx(
            0,
            wc.lpszClassName,
            string.Empty,
            0,
            0, 0, 0, 0,
            HWND_MESSAGE,
            IntPtr.Zero,
            wc.hInstance,
            IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            _ = HandleClipboardUpdateAsync();
            return IntPtr.Zero;
        }

        if (msg == WM_CLOSE)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private async Task HandleClipboardUpdateAsync()
    {
        try
        {
            // Fix 4: I/O outside the lock to prevent stalling
            var text = await _clipboardProvider.GetTextAsync();
            if (string.IsNullOrEmpty(text))
                return;

            var hash = _utils.ComputeHash(text);

            await _updateLock.WaitAsync();
            try
            {
                if (hash == _lastClipboardHash)
                    return;

                _lastClipboardHash = hash;
                OnClipboardChanged?.Invoke(text);
            }
            finally
            {
                _updateLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clipboard update handling failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true; // Prevent event fire

        // Fix 1: Removed Join(). Fire and forget the close message.
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        _updateLock.Dispose();
    }

    #region Win32

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_CLOSE = 0x0010;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    #endregion
}