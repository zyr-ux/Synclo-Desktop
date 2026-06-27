using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Synclo.Utilities;

namespace Synclo.Features.Clipboard_Manager.Clipboard_Monitor;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ClipboardMonitorWindows(
    IClipboardProvider clipboardProvider,
    ILogger<ClipboardMonitorWindows> logger,
    IUtils utils)
    : IClipboardMonitor, IDisposable
{
    private readonly IClipboardProvider _clipboardProvider = clipboardProvider ?? throw new ArgumentNullException(nameof(clipboardProvider));
    private readonly ILogger<ClipboardMonitorWindows> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IUtils _utils = utils ?? throw new ArgumentNullException(nameof(utils));
    private readonly object _startLock = new();

    private Thread? _messageThread;
    private IntPtr _hwnd;
    private volatile bool _disposed;
    private volatile bool _stopping;
    private volatile string? _lastClipboardHash;

    private TaskCompletionSource? _initTcs;
    private volatile bool _startedSuccessfully;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate? _wndProcDelegate;

    public event Action<string>? OnClipboardChanged;
    public event Action<Exception?>? OnMonitorStopped;

    public bool IsRunning => _messageThread is { IsAlive: true } && _startedSuccessfully;

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
        try
        {
            await _clipboardProvider.SetTextAsync(text);
            _lastClipboardHash = _utils.ComputeHash(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting clipboard text");
            throw;
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
            HandleClipboardUpdate();
            return IntPtr.Zero;
        }

        if (msg == WM_CLOSE)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void HandleClipboardUpdate()
    {
        try
        {
            var text = TryGetClipboardUnicodeTextNativeWithRetries();
            if (string.IsNullOrEmpty(text))
                return;

            var currentHash = _utils.ComputeHash(text);
            if (_lastClipboardHash == currentHash)
                return;

            _lastClipboardHash = currentHash;

            var capturedText = text;
            Task.Run(() => OnClipboardChanged?.Invoke(capturedText));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clipboard update handling failed");
        }
    }

    private static string? TryGetClipboardUnicodeTextNativeWithRetries()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var text = TryGetClipboardUnicodeTextNative();
            if (text != null)
                return text;

            Thread.Sleep(5);
        }

        return null;
    }

    private static string? TryGetClipboardUnicodeTextNative()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
                return null;

            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
                return null;

            var locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUni(locked);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;

        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    #region Win32

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_CLOSE = 0x0010;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const uint CF_UNICODETEXT = 13;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    #endregion
}
