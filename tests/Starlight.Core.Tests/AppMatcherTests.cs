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
