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
