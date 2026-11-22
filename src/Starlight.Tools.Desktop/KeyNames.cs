namespace Starlight.Tools.Desktop;

/// <summary>Key names the model may use in input_keys, to virtual-key codes. Plain logic, tested in the container.</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["windows"] = 0x5B, ["cmd"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["printscreen"] = 0x2C, ["capslock"] = 0x14, ["menu"] = 0x5D,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["mute"] = 0xAD, ["playpause"] = 0xB3, ["nexttrack"] = 0xB0, ["prevtrack"] = 0xB1,
        ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE,
    };

    public static ushort Code(string name)
    {
        var n = name.Trim();
        if (Named.TryGetValue(n, out var code))
        {
            return code;
        }
        if (n.Length == 1 && char.IsAsciiLetterOrDigit(n[0]))
        {
            return char.ToUpperInvariant(n[0]);
        }
        if (n.Length is 2 or 3 && (n[0] is 'f' or 'F') && int.TryParse(n[1..], out var f) && f is >= 1 and <= 24)
        {
            return (ushort)(0x70 + f - 1);
        }
        throw new ToolException($"Unknown key '{name}'. Use names like ctrl, alt, shift, win, enter, esc, tab, f5, a-z, 0-9.");
    }
}
