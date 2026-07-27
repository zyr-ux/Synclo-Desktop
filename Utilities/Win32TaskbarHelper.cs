using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Synclo.Utilities;

// Remove this when upgrading to Avalonia 12x sdk.
internal static class Win32TaskbarHelper
{
    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABS_AUTOHIDE = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCHITTEST = 0x0084;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0;
        public RECT rgrc1;
        public RECT rgrc2;
        public IntPtr lppos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    public static void EnableTaskbarAutoHideFix(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        IntPtr Hook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return HandleWndProc(window, hWnd, msg, wParam, lParam, ref handled);
        }

        window.Loaded += (s, e) => Win32Properties.AddWndProcHookCallback(window, Hook);
        window.Unloaded += (s, e) => Win32Properties.RemoveWndProcHookCallback(window, Hook);
    }

    private static IntPtr HandleWndProc(Window window, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (window.WindowState == WindowState.Maximized && OperatingSystem.IsWindows())
        {
            if (msg == WM_NCHITTEST)
            {
                if (GetTaskbarAutoHideEdge(out uint edge))
                {
                    int x = (short)(lParam.ToInt64() & 0xFFFF);
                    int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

                    IntPtr hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
                    MONITORINFO mi = default;
                    mi.cbSize = (uint)Marshal.SizeOf(mi);

                    if (GetMonitorInfo(hMonitor, ref mi))
                    {
                        bool isEdgeHover = edge switch
                        {
                            0 => x <= mi.rcMonitor.Left + 2,
                            1 => y <= mi.rcMonitor.Top + 2,
                            2 => x >= mi.rcMonitor.Right - 3,
                            3 => y >= mi.rcMonitor.Bottom - 3,
                            _ => false
                        };

                        if (isEdgeHover)
                        {
                            handled = true;
                            int hitResult = edge switch
                            {
                                0 => 10, // HTLEFT
                                1 => 12, // HTTOP
                                2 => 11, // HTRIGHT
                                3 => 15, // HTBOTTOM
                                _ => 15
                            };
                            return new IntPtr(hitResult);
                        }
                    }
                }
            }
            else if (msg == WM_GETMINMAXINFO)
            {
                if (GetTaskbarAutoHideEdge(out _))
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMaxSize.Y -= 1;
                    mmi.ptMaxTrackSize.Y -= 1;
                    Marshal.StructureToPtr(mmi, lParam, false);
                }
            }
            else if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
            {
                if (GetTaskbarAutoHideEdge(out uint edge))
                {
                    var paramsObj = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lParam);
                    if (edge == 3) paramsObj.rgrc0.Bottom -= 1;
                    else if (edge == 1) paramsObj.rgrc0.Top += 1;
                    else if (edge == 0) paramsObj.rgrc0.Left += 1;
                    else if (edge == 2) paramsObj.rgrc0.Right -= 1;
                    Marshal.StructureToPtr(paramsObj, lParam, false);
                }
            }
        }

        return IntPtr.Zero;
    }

    /// Checks if Windows Taskbar Auto-Hide is currently enabled and returns the taskbar edge.
    public static bool GetTaskbarAutoHideEdge(out uint edge)
    {
        edge = 3; // Default Bottom
        if (!OperatingSystem.IsWindows()) return false;

        APPBARDATA abd = new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        IntPtr state = SHAppBarMessage(ABM_GETSTATE, ref abd);
        bool isAutoHide = (state.ToInt64() & ABS_AUTOHIDE) != 0;

        if (isAutoHide)
        {
            abd.cbSize = (uint)Marshal.SizeOf<APPBARDATA>();
            SHAppBarMessage(ABM_GETTASKBARPOS, ref abd);
            edge = abd.uEdge;
        }

        return isAutoHide;
    }
}
