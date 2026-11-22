using Serilog;
using Starlight.Events;
using Starlight.Settings;

namespace Starlight.Model;

/// <summary>
/// Game mode (plan 5.2): while a game runs full screen, or a program from the list runs, the model server stops
/// to free the graphics card, and it starts again when the game ends. Two checks in a row turn it on and three
/// turn it off, so a quick alt-tab does not restart the model.
/// </summary>
public sealed class GameMode(SettingsStore settings, Func<bool> fullScreenGame, Func<IReadOnlyCollection<string>> runningProcesses)
{
    private int _seen;
    private int _gone;

    public bool Active { get; private set; }

    /// <summary>Raised with true when a game starts and false when it ends.</summary>
    public event Func<bool, string, Task>? Changed;

    /// <summary>Checks once. Returns the game's name when one is running.</summary>
    public string? Detect()
    {
        var s = settings.Current.General;
        if (!s.GameMode)
        {
            return null;
        }
        if (s.GameProcesses.Count > 0)
        {
            var running = runningProcesses();
            var listed = s.GameProcesses.Select(p => Path.GetFileNameWithoutExtension(p.Trim())).FirstOrDefault(p => p.Length > 0 && running.Contains(p, StringComparer.OrdinalIgnoreCase));
            if (listed is not null)
            {
                return listed;
            }
        }
        return fullScreenGame() ? "a full-screen game" : null;
    }

    public async Task TickAsync()
    {
        var game = Detect();
        if (game is not null)
        {
            _gone = 0;
            if (!Active && ++_seen >= 2)
            {
                Active = true;
                Log.Information("Game mode on: {Game}", game);
                await RaiseAsync(true, game);
            }
        }
        else
        {
            _seen = 0;
            if (Active && ++_gone >= 3)
            {
                Active = false;
                Log.Information("Game mode off");
                await RaiseAsync(false, "");
            }
        }
    }

    private async Task RaiseAsync(bool on, string game)
    {
        if (Changed is { } handler)
        {
            foreach (var h in handler.GetInvocationList().Cast<Func<bool, string, Task>>())
            {
                try
                {
                    await h(on, game);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Game mode change failed");
                }
            }
        }
    }
}
