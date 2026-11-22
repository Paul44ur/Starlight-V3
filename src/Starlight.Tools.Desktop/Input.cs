using System.Runtime.InteropServices;

namespace Starlight.Tools.Desktop;

/// <summary>Mouse and keyboard through SendInput, as the logged-in user. Held keys are released by the kill switch.</summary>
public static partial class Input
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
    private const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10,
        MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] INPUT[] inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    private static readonly HashSet<ushort> Held = [];
    private static readonly Lock Gate = new();
    private static long s_ownInputAt;

    private static void Send(params INPUT[] inputs)
    {
        Interlocked.Exchange(ref s_ownInputAt, Environment.TickCount64);
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
        {
            throw new ToolException("Windows blocked the input (an admin window may be in front).");
        }
    }

    /// <summary>True when the user pressed a key or moved the mouse in the last <paramref name="window"/> (not counting our own input).</summary>
    public static bool UserIsActive(TimeSpan window)
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return false;
        }
        var now = (uint)Environment.TickCount;
        var sinceInput = now - info.dwTime;
        var sinceOwn = Environment.TickCount64 - Interlocked.Read(ref s_ownInputAt);
        return sinceInput < window.TotalMilliseconds && sinceOwn > window.TotalMilliseconds + 200;
    }

    public static void MoveTo(int x, int y)
    {
        var vd = ScreenCapture.VirtualDesktop();
        var nx = (int)Math.Round((x - vd.X) * 65535.0 / Math.Max(1, vd.Width - 1));
        var ny = (int)Math.Round((y - vd.Y) * 65535.0 / Math.Max(1, vd.Height - 1));
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
    }

    public static void Click(int x, int y, string button = "left", bool doubleClick = false)
    {
        MoveTo(x, y);
        var (down, up) = button switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        for (var i = 0; i < (doubleClick ? 2 : 1); i++)
        {
            Send(Mouse(down, 0, 0), Mouse(up, 0, 0));
        }
    }

    public static void Scroll(int clicks)
    {
        var m = Mouse(MOUSEEVENTF_WHEEL, 0, 0);
        m.u.mi.mouseData = unchecked((uint)(clicks * 120));
        Send(m);
    }

    /// <summary>Types text as Unicode characters (layout-independent).</summary>
    public static void Type(string text, CancellationToken ct)
    {
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\n')
            {
                Tap(0x0D);
                continue;
            }
            Send(Key(0, ch, KEYEVENTF_UNICODE), Key(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            Thread.Sleep(4);
        }
    }

    /// <summary>A shortcut like "ctrl+s", "alt+f4", "win+d", "enter", "ctrl+shift+esc".</summary>
    public static void Keys(string combo)
    {
        var parts = combo.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var codes = parts.Select(KeyNames.Code).ToList();
        try
        {
            foreach (var c in codes)
            {
                Down(c);
            }
        }
        finally
        {
            for (var i = codes.Count - 1; i >= 0; i--)
            {
                Up(codes[i]);
            }
        }
    }

    public static void Tap(ushort vk)
    {
        Down(vk);
        Up(vk);
    }

    private static void Down(ushort vk)
    {
        lock (Gate) { Held.Add(vk); }
        Send(Key(vk, '\0', 0));
    }

    private static void Up(ushort vk)
    {
        Send(Key(vk, '\0', KEYEVENTF_KEYUP));
        lock (Gate) { Held.Remove(vk); }
    }

    /// <summary>Kill-switch cleanup: lets go of every key and mouse button we may hold.</summary>
    public static void ReleaseAll()
    {
        ushort[] keys;
        lock (Gate) { keys = [.. Held]; Held.Clear(); }
        foreach (var k in keys.Concat(new ushort[] { 0x10, 0x11, 0x12, 0x5B }))
        {
            try { SendInput(1, [Key(k, '\0', KEYEVENTF_KEYUP)], Marshal.SizeOf<INPUT>()); } catch (Exception) { }
        }
        SendInput(3, [Mouse(MOUSEEVENTF_LEFTUP, 0, 0), Mouse(MOUSEEVENTF_RIGHTUP, 0, 0), Mouse(MOUSEEVENTF_MIDDLEUP, 0, 0)], Marshal.SizeOf<INPUT>());
    }

    private static INPUT Mouse(uint flags, int x, int y) =>
        new() { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = flags } } };

    private static INPUT Key(ushort vk, char scan, uint flags) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
}
