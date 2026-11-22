using System.Runtime.InteropServices;

namespace Starlight.Tools.Desktop;

/// <summary>Watches a held key, for push-to-talk (RegisterHotKey only reports the press).</summary>
public static partial class KeyState
{
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vk);

    public static bool IsDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    /// <summary>Completes when the key is released (checked every 30 ms), or after the limit.</summary>
    public static async Task WaitForReleaseAsync(uint vk, TimeSpan limit, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + limit;
        while (IsDown(vk) && DateTime.UtcNow < until)
        {
            await Task.Delay(30, ct);
        }
    }
}
