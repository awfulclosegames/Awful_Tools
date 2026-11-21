using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AwfulDiff.Services;

/// <summary>
/// Pins the maximized rect of borderless (WindowStyle="None") windows to the monitor's work
/// area. Win32's default maximize sizes a resizable window to the work area PLUS the invisible
/// resize-border thickness, so a frameless window lands a few pixels off-screen (left edge
/// clipped) with its bottom under the taskbar. We answer WM_GETMINMAXINFO ourselves with the
/// exact work-area rect. Call <see cref="Enforce"/> once per window (constructor).
/// </summary>
public static class WorkAreaMaximize
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point PtReserved, PtMaxSize, PtMaxPosition, PtMinTrackSize, PtMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor, RcWork;
        public uint DwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    /// <summary>Installs the WM_GETMINMAXINFO hook on the window's HwndSource.</summary>
    public static void Enforce(Window window)
    {
        window.SourceInitialized += (_, _) =>
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // Maximize to the exact work area of the monitor the window is on (per-monitor taskbar
        // offset — dragging a maximized window across monitors re-queries the new one).
        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon != IntPtr.Zero && GetMonitorInfo(hMon, ref info))
        {
            mmi.PtMaxPosition = new Point { X = info.RcWork.Left, Y = info.RcWork.Top };
            mmi.PtMaxSize = new Point
            {
                X = info.RcWork.Right - info.RcWork.Left,
                Y = info.RcWork.Bottom - info.RcWork.Top
            };
        }

        // We swallow the message, so WPF's own handler never runs — re-apply MinWidth/MinHeight
        // as the manual-resize floor (WM_GETMINMAXINFO is what enforces them at the frame level).
        if (HwndSource.FromHwnd(hwnd)?.RootVisual is Window w
            && (!double.IsNaN(w.MinWidth) || !double.IsNaN(w.MinHeight)))
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(w);
            mmi.PtMinTrackSize = new Point
            {
                X = !double.IsNaN(w.MinWidth) ? (int)Math.Ceiling(w.MinWidth * dpi.DpiScaleX) : 0,
                Y = !double.IsNaN(w.MinHeight) ? (int)Math.Ceiling(w.MinHeight * dpi.DpiScaleY) : 0
            };
        }

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }
}
