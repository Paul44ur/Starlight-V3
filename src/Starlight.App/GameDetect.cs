using System.Runtime.InteropServices;

namespace Starlight.App;

/// <summary>Asks Windows whether a Direct3D program runs full screen (a game, not a video or a slide show).</summary>
internal static partial class GameDetect
{
    private const int QunsRunningD3DFullScreen = 3;

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    public static bool D3DFullScreen() => SHQueryUserNotificationState(out var s) == 0 && s == QunsRunningD3DFullScreen;
}
