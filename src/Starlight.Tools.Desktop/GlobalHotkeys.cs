using System.Runtime.InteropServices;
using Serilog;

namespace Starlight.Tools.Desktop;

/// <summary>System-wide hotkeys (the kill switch, push-to-talk). A background thread owns the
/// registrations and a message loop, so a hotkey fires even while the UI thread is busy.</summary>
public sealed partial class GlobalHotkeys : IDisposable
{
    private readonly Thread _thread;
    private readonly List<(int Id, string Text, Action OnPress)> _pending;
    private readonly TaskCompletionSource<List<string>> _registered = new();
    private uint _threadId;

    public GlobalHotkeys(IEnumerable<(string Hotkey, Action OnPress)> hotkeys)
    {
        _pending = hotkeys.Select((h, i) => (i + 1, h.Hotkey, h.OnPress)).ToList();
        _thread = new Thread(Run) { IsBackground = true, Name = "Starlight hotkeys" };
        _thread.Start();
    }

    /// <summary>Hotkeys that could not be registered (another program owns them).</summary>
    public Task<List<string>> Failed => _registered.Task;

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var failed = new List<string>();
        var actions = new Dictionary<int, Action>();
        foreach (var (id, text, onPress) in _pending)
        {
            try
            {
                var (mods, key) = HotkeyParser.Parse(text);
                if (RegisterHotKey(0, id, mods, key))
                {
                    actions[id] = onPress;
                    Log.Information("Hotkey {Hotkey} registered", text);
                }
                else
                {
                    failed.Add(text);
                    Log.Warning("Hotkey {Hotkey} is taken by another program", text);
                }
            }
            catch (FormatException ex)
            {
                failed.Add(text);
                Log.Warning("Hotkey {Hotkey} is not valid: {Reason}", text, ex.Message);
            }
        }
        _registered.SetResult(failed);
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY && actions.TryGetValue((int)msg.wParam, out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Hotkey handler failed");
                }
            }
        }
        foreach (var id in actions.Keys)
        {
            UnregisterHotKey(0, id);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        }
        _thread.Join(1000);
    }

    private const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
