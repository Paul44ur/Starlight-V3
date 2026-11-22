using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Starlight.Tools.Desktop;

public sealed record WindowInfo(nint Handle, string Title, string Process, int ProcessId, bool Minimized);

/// <summary>The few Win32 calls the desktop tools need.</summary>
internal static partial class Win32
{
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetWindowText(nint hwnd, char* text, int max);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(nint hwnd, int index);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    internal const int SW_RESTORE = 9;
    internal const uint WM_CLOSE = 0x0010;
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;

    /// <summary>The windows a user sees in Alt+Tab: visible, titled, not owned, not tool windows, not cloaked.</summary>
    public static List<WindowInfo> TopLevelWindows()
    {
        var result = new List<WindowInfo>();
        var own = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != 0
                || (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }
            var title = Title(hwnd);
            if (title.Length == 0)
            {
                return true;
            }
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own)
            {
                return true;
            }
            result.Add(new WindowInfo(hwnd, title, ProcessName((int)pid), (int)pid, IsIconic(hwnd)));
            return true;
        }, 0);
        return result;
    }

    private static unsafe string Title(nint hwnd)
    {
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0)
        {
            return "";
        }
        var buffer = stackalloc char[Math.Min(len + 1, 1024)];
        var n = GetWindowText(hwnd, buffer, Math.Min(len + 1, 1024));
        return new string(buffer, 0, n);
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            return "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>Brings a window to the front. Windows only allows this from the foreground thread,
    /// so the call briefly attaches to it.</summary>
    public static bool Focus(nint hwnd)
    {
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(me, fgThread, false);
            }
        }
    }
}
