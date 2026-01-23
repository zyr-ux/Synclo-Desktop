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

    private Thread? _messageThread;
    private IntPtr _hwnd;
    private bool _disposed;
    private string? _lastClipboardHash;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate? _wndProcDelegate;

    public event Action<string>? OnClipboardChanged;

    public bool IsRunning => _messageThread is { IsAlive: true };

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
        if (IsRunning)
            return Task.CompletedTask;

        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "SyncloClipboardMonitorWindows"
        };

        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        _messageThread = null;
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
            _logger.LogError(ex, "Failed to set clipboard text");
            throw;
        }
    }

    private void MessageLoop()
    {
        _hwnd = CreateMessageWindow();

        if (_hwnd == IntPtr.Zero)
        {
            _logger.LogError("ClipboardMonitorWindows: Failed to create message window");
            return;
        }

        if (!AddClipboardFormatListener(_hwnd))
        {
            _logger.LogError("ClipboardMonitorWindows: AddClipboardFormatListener failed");
            return;
        }

        try
        {
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            RemoveClipboardFormatListener(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
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

        if (RegisterClassEx(ref wc) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("RegisterClassEx failed with error {Error}", err);
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
            var text = await _clipboardProvider.GetTextAsync();
            if (string.IsNullOrEmpty(text))
                return;

            var hash = _utils.ComputeHash(text);
            if (hash == _lastClipboardHash)
                return;

            _lastClipboardHash = hash;
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

        _disposed = true;
        StopAsync().Wait();
    }

    #region Win32

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_CLOSE = 0x0010;
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

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

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