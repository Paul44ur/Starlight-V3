using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Starlight.Overlay;

internal static partial class Native
{
    public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    private const int GWL_EXSTYLE = -20;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOZORDER = 0x4;
    private static readonly nint HWND_TOPMOST = -1;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT rect, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
        public fixed char Device[32];
    }

    public static unsafe List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref RECT _, nint _) =>
        {
            var info = new MONITORINFOEX { Size = sizeof(MONITORINFOEX) };
            if (GetMonitorInfo(monitor, ref info))
            {
                var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
                var r = info.Monitor;
                list.Add(new MonitorInfo(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, scale, (info.Flags & 1) != 0, new string(info.Device)));
            }
            return true;
        }, 0);
        return list;
    }

    public static nint Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>Never takes focus, no taskbar or Alt+Tab entry, not in screenshots; clicks pass through when <paramref name="clickThrough"/>.</summary>
    public static void MakeOverlay(Window w, bool clickThrough)
    {
        var h = Handle(w);
        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
        ex = clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(h, GWL_EXSTYLE, (nint)ex);
        SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Places a window in physical pixels (WPF's Left/Top are in units of the window's own DPI, which is wrong across monitors).</summary>
    public static void Place(Window w, int x, int y, int width, int height) =>
        SetWindowPos(Handle(w), HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    /// <summary>Keeps the window above others again (a full-screen app may have pushed it down).</summary>
    public static void BringToTop(Window w) =>
        SetWindowPos(Handle(w), HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | 0x1 | 0x2);
}
