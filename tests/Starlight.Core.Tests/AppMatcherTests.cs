using Starlight.Tools.Desktop;

namespace Starlight.Tests;

public sealed class AppMatcherTests
{
    private static readonly AppEntry[] Apps =
    [
        new("Word", "word.lnk", AppKind.Shortcut),
        new("Word Viewer", "wv.lnk", AppKind.Shortcut),
        new("Spotify", "spotify.lnk", AppKind.Shortcut),
        new("Spotify", "SpotifyAB.Spotify!App", AppKind.StoreApp),
        new("Visual Studio Code", "code.lnk", AppKind.Shortcut),
        new("Microsoft Edge", "edge.lnk", AppKind.Shortcut),
    ];

    [Theory]
    [InlineData("word", "word.lnk")]
    [InlineData("the Spotify app", "spotify.lnk")]
    [InlineData("visual studio", "code.lnk")]
    [InlineData("studio code", "code.lnk")]
    [InlineData("edge", "edge.lnk")]
    public void Picks_the_meant_app(string spoken, string target)
    {
        Assert.Equal(target, AppMatcher.Find(spoken, Apps)[0].Target);
    }

    [Fact]
    public void Unknown_name_finds_nothing_and_builtins_resolve()
    {
        Assert.Empty(AppMatcher.Find("photoshop", Apps));
        Assert.Equal("calc", AppMatcher.Builtin("Calculator")!.Target);
        Assert.Null(AppMatcher.Builtin("spotify"));
    }
}

public sealed class HotkeyParserTests
{
    [Fact]
    public void Parses_modifiers_and_keys()
    {
        Assert.Equal((HotkeyParser.ModControl | HotkeyParser.ModAlt | HotkeyParser.ModNoRepeat, (uint)'J'), HotkeyParser.Parse("Ctrl+Alt+J"));
        Assert.Equal(0x20u, HotkeyParser.Parse("ctrl + alt + space").Key);
        Assert.Equal(0x7Bu, HotkeyParser.Parse("Shift+F12").Key);
    }

    [Theory]
    [InlineData("J")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+J+K")]
    [InlineData("Ctrl+Banana")]
    public void Rejects_bad_hotkeys(string text) => Assert.Throws<FormatException>(() => HotkeyParser.Parse(text));
}

public sealed class TargetParserTests
{
    [Theory]
    [InlineData("7", 7)]
    [InlineData("[12]", 12)]
    [InlineData("element 3", 3)]
    [InlineData("#5", 5)]
    public void Element_numbers(string text, int n) => Assert.Equal(n, TargetParser.Parse(text).Element);

    [Fact]
    public void Points_and_names()
    {
        Assert.Equal((100, -20), TargetParser.Parse("100, -20").Point);
        Assert.Equal((5, 6), TargetParser.Parse("(5,6)").Point);
        Assert.Equal("Save as", TargetParser.Parse("\"Save as\"").Name);
    }

    [Fact]
    public void Name_matching_prefers_exact()
    {
        Assert.Equal(3, TargetParser.Match("save", "Save"));
        Assert.Equal(2, TargetParser.Match("save", "Save as…"));
        Assert.Equal(1, TargetParser.Match("settings app", "Open the Settings app now"));
        Assert.Equal(0, TargetParser.Match("print", "Save"));
    }
}

public sealed class KeyNamesTests
{
    [Fact]
    public void Names_and_letters_map_to_codes()
    {
        Assert.Equal(0x11, KeyNames.Code("Ctrl"));
        Assert.Equal('S', KeyNames.Code("s"));
        Assert.Equal(0x74, KeyNames.Code("F5"));
        Assert.Throws<Starlight.Tools.ToolException>(() => KeyNames.Code("banana"));
    }
}
