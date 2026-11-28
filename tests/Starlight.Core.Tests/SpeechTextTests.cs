using Starlight.Voice;

namespace Starlight.Tests;

public sealed class SpeechTextTests
{
    private static List<string> Stream(params string[] deltas)
    {
        var s = new SentenceSplitter();
        var all = deltas.SelectMany(s.Add).ToList();
        if (s.Flush() is { } rest)
        {
            all.Add(rest);
        }
        return all;
    }

    [Fact]
    public void Splits_streamed_text_into_sentences_as_they_complete()
    {
        var s = new SentenceSplitter();
        Assert.Empty(s.Add("Notepad is "));
        Assert.Equal(["Notepad is open."], s.Add("open. What"));
        Assert.Empty(s.Add(" next"));
        Assert.Equal(["What next?"], s.Add("? "));
        Assert.Null(s.Flush());
    }

    [Fact]
    public void Keeps_numbers_and_abbreviations_together()
    {
        Assert.Equal(["Version 3.5 is out, e.g. on Windows.", "Done"], Stream("Version 3.5 is out, e.g. on Windows. Done"));
        Assert.Equal(["It costs 4.99 euros."], Stream("It costs 4.", "99 euros."));
    }

    [Fact]
    public void Speech_text_drops_markdown_links_and_code()
    {
        Assert.Equal("See Docs and a link. Done.", SpeechText.ForSpeech("See **[Docs](https://x.y)** and https://a.b/c. ```code``` Done."));
        Assert.Equal("one two", SpeechText.ForSpeech("- one\n- two"));
    }

    [Theory]
    [InlineData("Thank you.", true)]
    [InlineData("[BLANK_AUDIO]", true)]
    [InlineData(" ", true)]
    [InlineData("Open Notepad.", false)]
    public void Whisper_noise_is_recognised(string text, bool noise) => Assert.Equal(noise, SpeechText.IsNoise(text));
}

public sealed class AudioMathTests
{
    [Fact]
    public void Stereo_pcm16_becomes_mono_float()
    {
        short[] interleaved = [16384, 16384, -32768, 0];
        var bytes = new byte[interleaved.Length * 2];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
        var mono = AudioMath.ToMono(bytes, channels: 2, isFloat: false, bitsPerSample: 16);
        Assert.Equal([0.5f, -0.5f], mono);
    }

    [Fact]
    public void Resampling_keeps_duration_and_a_low_tone()
    {
        var input = Enumerable.Range(0, 48000).Select(i => (float)Math.Sin(2 * Math.PI * 200 * i / 48000)).ToArray();
        var output = AudioMath.Resample(input, 48000, 16000);
        Assert.Equal(16000, output.Length);
        Assert.InRange(AudioMath.Level(output), AudioMath.Level(input) - 0.05, AudioMath.Level(input) + 0.01);
        Assert.Equal(48000, AudioMath.Resample(output, 16000, 48000).Length);
    }

    [Fact]
    public void Level_is_zero_for_silence_and_high_for_loud()
    {
        Assert.Equal(0, AudioMath.Level(new float[100]));
        Assert.True(AudioMath.Level(Enumerable.Repeat(0.5f, 100).ToArray()) > 0.8);
    }
}

public sealed class VoiceApprovalTests
{
    [Theory]
    [InlineData("Yes.", Starlight.Tools.ApprovalAnswer.Yes)]
    [InlineData("go ahead", Starlight.Tools.ApprovalAnswer.Yes)]
    [InlineData("No!", Starlight.Tools.ApprovalAnswer.No)]
    [InlineData("Always allow it", Starlight.Tools.ApprovalAnswer.Always)]
    [InlineData("open notepad", null)]
    [InlineData("yes, and also close Word", null)]
    [InlineData("Yes, please.", Starlight.Tools.ApprovalAnswer.Yes)]
    [InlineData("Yes, do it.", Starlight.Tools.ApprovalAnswer.Yes)]
    [InlineData("No, thanks.", Starlight.Tools.ApprovalAnswer.No)]
    [InlineData("Don\u2019t.", Starlight.Tools.ApprovalAnswer.No)]
    [InlineData("Yes?", Starlight.Tools.ApprovalAnswer.Yes)]
    [InlineData("Always?", null)]
    public void Parses_spoken_answers(string heard, Starlight.Tools.ApprovalAnswer? expected) =>
        Assert.Equal(expected, VoiceApproval.Parse(heard));
}

public sealed class WakeTokensTests
{
    private static readonly HashSet<string> Vocab = WakeTokens.ReadVocab(
        ["<blk> 0", "▁HE 1", "Y 2", "▁STAR 3", "L 4", "IGHT 5", "▁ 6", "N 7", "O 8", "VA 9", "▁HEY 10", "S 11", "T 12", "A 13", "R 14"]);

    [Fact]
    public void Phrase_becomes_longest_pieces_with_label()
    {
        Assert.Equal("▁HEY ▁STAR L IGHT :1.5 #0.25 @HEY_STARLIGHT", WakeTokens.Line("Hey, Starlight!", Vocab));
    }

    [Fact]
    public void Words_without_a_start_piece_use_the_lone_mark()
    {
        Assert.Equal("▁ N O VA :1.5 #0.25 @NOVA", WakeTokens.Line("nova", Vocab));
        Assert.Null(WakeTokens.Line("Hey Zed", Vocab));
        Assert.Null(WakeTokens.Line("!!", Vocab));
    }

    [Fact]
    public void Sensitivity_lowers_the_threshold()
    {
        Assert.Equal(0.45, WakeTokens.Threshold(0));
        Assert.Equal(0.1, WakeTokens.Threshold(1));
        Assert.True(WakeTokens.Threshold(0.5) is > 0.1 and < 0.45);
    }
}

public sealed class SpokenCodeTests
{
    [Theory]
    [InlineData("four two seven, one nine nine.", "427199")]
    [InlineData("The code is 4271 99", "427199")]
    [InlineData("oh one two three", "0123")]
    public void Spoken_digits_become_a_code(string heard, string code) =>
        Assert.Equal(code, Starlight.Voice.SpokenCode.Parse(heard));

    [Theory]
    [InlineData("yes")]
    [InlineData("open notepad")]
    [InlineData("one two")]
    [InlineData("call me at four five six seven later")]
    public void Other_speech_is_not_a_code(string heard) =>
        Assert.Null(Starlight.Voice.SpokenCode.Parse(heard));
}

public sealed class SpeechFixTests
{
    private static List<string> SplitAll(params string[] deltas)
    {
        var s = new Starlight.Voice.SentenceSplitter();
        var all = deltas.SelectMany(d => s.Add(d)).ToList();
        if (s.Flush() is { } rest)
        {
            all.Add(rest);
        }
        return all;
    }

    [Fact]
    public void A_streamed_code_block_is_not_spoken()
    {
        var said = SplitAll("Here it is:\n``", "`python\nprint('a')\n", "x = 1\n`", "``\nDone. Bye now.");
        Assert.DoesNotContain(said, x => x.Contains("print") || x.Contains("x = 1") || x.Contains('`'));
        Assert.Contains("Done.", said);
        Assert.Contains("Bye now.", said);
    }

    [Fact]
    public void An_unclosed_code_block_is_dropped_at_the_end()
    {
        var said = SplitAll("Look.\n```\nsecret code");
        Assert.Equal(["Look."], said);
    }

    [Fact]
    public void No_ends_a_sentence()
    {
        Assert.Equal(["The answer is no.", "Sorry."], SplitAll("The answer is no. Sorry."));
    }

    [Fact]
    public void Only_heading_marks_are_removed()
    {
        Assert.Equal("Title Issue #12 is open.", Starlight.Voice.SpeechText.ForSpeech("## Title\nIssue #12 is open."));
        Assert.Equal("See this.", Starlight.Voice.SpeechText.ForSpeech("See ![chart](http://x/a.png) this."));
    }

    [Theory]
    [InlineData("always", true)]
    [InlineData("Always allow it.", true)]
    [InlineData("always ask me first", false)]
    [InlineData("always no", false)]
    public void Only_a_clear_phrase_is_always(string heard, bool always) =>
        Assert.Equal(always, Starlight.Voice.VoiceApproval.Parse(heard) == Starlight.Tools.ApprovalAnswer.Always);
}
