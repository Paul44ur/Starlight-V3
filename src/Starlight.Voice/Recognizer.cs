using System.Diagnostics;
using System.Text;
using Serilog;
using SherpaOnnx;
using Whisper.net;

namespace Starlight.Voice;

/// <summary>Speech to text: Silero VAD keeps only the speech, whisper.cpp (Vulkan) writes it down, in English.</summary>
public sealed class Recognizer : IDisposable
{
    private readonly WhisperFactory _whisper;
    private readonly WhisperProcessor _processor;
    private readonly VadModelConfig _vadConfig;
    private readonly SemaphoreSlim _one = new(1, 1);

    public Recognizer(string whisperModel, string vadModel)
    {
        var sw = Stopwatch.StartNew();
        _whisper = WhisperFactory.FromPath(whisperModel);
        _processor = _whisper.CreateBuilder().WithLanguage("en").WithNoContext().WithThreads(Math.Max(2, Environment.ProcessorCount / 2)).Build();
        _vadConfig = new VadModelConfig
        {
            SampleRate = Microphone.SampleRate,
            NumThreads = 1,
            SileroVad = new SileroVadModelConfig
            {
                Model = vadModel,
                Threshold = 0.5f,
                MinSilenceDuration = 0.6f, // plan 5.3: end of speech after 600 ms of quiet
                MinSpeechDuration = 0.2f,
                MaxSpeechDuration = 30f,
                WindowSize = 512,
            },
        };
        Log.Information("Speech recognition loaded in {Ms} ms", sw.ElapsedMilliseconds);
    }

    /// <summary>The speech parts of the audio, joined. Empty if nobody spoke.</summary>
    public float[] SpeechOnly(float[] audio)
    {
        using var vad = new VoiceActivityDetector(_vadConfig, 60);
        const int window = 512;
        for (var i = 0; i + window <= audio.Length; i += window)
        {
            vad.AcceptWaveform(audio[i..(i + window)]);
        }
        vad.Flush();
        var speech = new List<float>();
        while (!vad.IsEmpty())
        {
            speech.AddRange(vad.Front().Samples);
            vad.Pop();
        }
        return speech.ToArray();
    }

    public async Task<string> TranscribeAsync(float[] audio, CancellationToken ct)
    {
        var speech = SpeechOnly(audio);
        if (speech.Length < Microphone.SampleRate / 4)
        {
            return "";
        }
        // Whisper does better with a little silence around the speech.
        var padded = new float[speech.Length + Microphone.SampleRate / 2];
        Array.Copy(speech, 0, padded, Microphone.SampleRate / 4, speech.Length);
        await _one.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var text = new StringBuilder();
            await foreach (var segment in _processor.ProcessAsync(padded, ct))
            {
                text.Append(segment.Text);
            }
            var result = text.ToString().Trim();
            Log.Information("Heard {Text} ({Seconds:0.0} s of speech in {Ms} ms)", result, speech.Length / (double)Microphone.SampleRate, sw.ElapsedMilliseconds);
            return SpeechText.IsNoise(result) ? "" : result;
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose()
    {
        _processor.Dispose();
        _whisper.Dispose();
        _one.Dispose();
    }
}
