using System.Diagnostics;
using System.Text;
using Serilog;
using SherpaOnnx;
using Whisper.net;

namespace Starlight.Voice;

/// <summary>Speech to text: Silero VAD keeps only the speech, whisper.cpp (Vulkan) writes it down, in English.</summary>
public sealed class Recognizer : IDisposable
{
    private readonly string _whisperModel;
    private WhisperFactory? _whisper;
    private WhisperProcessor? _processor;
    private readonly VadModelConfig _vadConfig;
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Game mode: whisper leaves the graphics card and only loads for one request at a time.</summary>
    public bool KeepUnloaded { get; private set; }

    public Recognizer(string whisperModel, string vadModel)
    {
        _whisperModel = whisperModel;
        Load();
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
    }

    private WhisperProcessor Load()
    {
        if (_processor is not null)
        {
            return _processor;
        }
        var sw = Stopwatch.StartNew();
        _whisper = WhisperFactory.FromPath(_whisperModel);
        _processor = _whisper.CreateBuilder().WithLanguage("en").WithNoContext().WithThreads(Math.Max(2, Environment.ProcessorCount / 2)).Build();
        Log.Information("Speech recognition loaded in {Ms} ms", sw.ElapsedMilliseconds);
        return _processor;
    }

    private void Unload()
    {
        _processor?.Dispose();
        _whisper?.Dispose();
        _processor = null;
        _whisper = null;
    }

    /// <summary>Frees the graphics card (true) or loads whisper again (false).</summary>
    public async Task SetUnloadedAsync(bool unloaded)
    {
        await _one.WaitAsync();
        try
        {
            KeepUnloaded = unloaded;
            if (unloaded)
            {
                Unload();
                Log.Information("Speech recognition unloaded for game mode");
            }
            else
            {
                Load();
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>A detector for hands-free listening: tells when the user has finished speaking.</summary>
    public Endpointer CreateEndpointer() => new(_vadConfig);

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
            await foreach (var segment in Load().ProcessAsync(padded, ct))
            {
                text.Append(segment.Text);
            }
            var result = text.ToString().Trim();
            Log.Information("Heard {Text} ({Seconds:0.0} s of speech in {Ms} ms)", result, speech.Length / (double)Microphone.SampleRate, sw.ElapsedMilliseconds);
            return SpeechText.IsNoise(result) ? "" : result;
        }
        finally
        {
            if (KeepUnloaded)
            {
                Unload();
            }
            _one.Release();
        }
    }

    public void Dispose()
    {
        Unload();
        _one.Dispose();
    }
}

/// <summary>Feeds live audio to Silero VAD; <see cref="Feed"/> returns true once speech started and then 600 ms of quiet followed.</summary>
public sealed class Endpointer : IDisposable
{
    private const int Window = 512;
    private readonly VoiceActivityDetector _vad;
    private readonly List<float> _pending = [];

    internal Endpointer(VadModelConfig config) => _vad = new VoiceActivityDetector(config, 30);

    public bool SpeechStarted { get; private set; }

    public bool Feed(float[] chunk)
    {
        _pending.AddRange(chunk);
        var ended = false;
        while (_pending.Count >= Window)
        {
            _vad.AcceptWaveform(_pending.GetRange(0, Window).ToArray());
            _pending.RemoveRange(0, Window);
            SpeechStarted |= _vad.IsSpeechDetected();
            if (!_vad.IsEmpty())
            {
                ended = true; // a complete segment: speech, then the minimum silence
                _vad.Clear();
            }
        }
        return ended;
    }

    public void Dispose() => _vad.Dispose();
}
