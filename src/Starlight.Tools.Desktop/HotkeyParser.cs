namespace Starlight.Tools.Desktop;

/// <summary>Turns "Ctrl+Alt+J" into the modifier flags and virtual key code RegisterHotKey wants.</summary>
public static class HotkeyParser
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    public static (uint Modifiers, uint Key) Parse(string text)
    {
        uint mods = ModNoRepeat;
        uint? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": mods |= ModControl; break;
                case "ALT": mods |= ModAlt; break;
                case "SHIFT": mods |= ModShift; break;
                case "WIN": case "WINDOWS": mods |= ModWin; break;
                default:
                    if (key is not null)
                    {
                        throw new FormatException($"'{text}' has more than one key.");
                    }
                    key = KeyCode(raw);
                    break;
            }
        }
        if (key is null)
        {
            throw new FormatException($"'{text}' has no key.");
        }
        if ((mods & ~ModNoRepeat) == 0)
        {
            throw new FormatException($"'{text}' needs Ctrl, Alt, Shift or Win.");
        }
        return (mods, key.Value);
    }

    private static uint KeyCode(string key)
    {
        var k = key.ToUpperInvariant();
        if (k.Length == 1 && (char.IsAsciiLetterUpper(k[0]) || char.IsAsciiDigit(k[0])))
        {
            return k[0];
        }
        if (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }
        return k switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "PAUSE" => 0x13,
            "HOME" => 0x24,
            "END" => 0x23,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            _ => throw new FormatException($"Unknown key '{key}'."),
        };
    }
}
