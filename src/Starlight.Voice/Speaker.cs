using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;
using SherpaOnnx;
using Starlight.Events;

namespace Starlight.Voice;

/// <summary>
/// Text to speech with Kokoro (through sherpa-onnx, on the CPU). Sentences are queued; the next one is
/// made while the current one plays. Stop() silences it at once (barge-in, "stop", the kill switch).
/// </summary>
public sealed class Speaker : IDisposable
{
    /// <summary>Kokoro v1.0 English voice names in the order of its voices.bin (overridable by voices.txt).</summary>
    public static readonly string[] DefaultVoices =
    [
        "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
        "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
        "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george", "bm_lewis",
    ];

    private readonly OfflineTts _tts;
    private readonly EventHub _events;
    private readonly string[] _voices;
    private readonly BlockingCollection<(string Text, long Generation)> _queue = new();
    private readonly Thread _worker;
    private readonly WasapiOut _output;
    private readonly FloatQueueProvider _playback;
    private long _generation;
    private int _pending;

    public Speaker(string kokoroDir, EventHub events)
    {
        _events = events;
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = Path.Combine(kokoroDir, "model.onnx");
        config.Model.Kokoro.Voices = Path.Combine(kokoroDir, "voices.bin");
        config.Model.Kokoro.Tokens = Path.Combine(kokoroDir, "tokens.txt");
        var espeak = Path.Combine(kokoroDir, "espeak-ng-data");
        if (Directory.Exists(espeak))
        {
            config.Model.Kokoro.DataDir = espeak;
        }
        var lexicon = Path.Combine(kokoroDir, "lexicon-us-en.txt");
        if (File.Exists(lexicon))
        {
            config.Model.Kokoro.Lexicon = lexicon;
            config.Model.Kokoro.Lang = "en-us";
        }
        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        _tts = new OfflineTts(config);
        var voicesFile = Path.Combine(kokoroDir, "voices.txt");
        _voices = File.Exists(voicesFile) ? File.ReadAllLines(voicesFile).Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToArray() : DefaultVoices;
        Log.Information("Voice loaded: {Speakers} speakers at {Rate} Hz", _tts.NumSpeakers, _tts.SampleRate);

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        WaveFormat mix;
        using (var client = device.CreateAudioClient())
        {
            mix = client.MixFormat;
        }
        _playback = new FloatQueueProvider(WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels), events);
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
        _output.Init(_playback);
        _output.Play();

        _worker = new Thread(Work) { IsBackground = true, Name = "Starlight speech" };
        _worker.Start();
    }

    public string Voice { get; set; } = "af_heart";
    public float Rate { get; set; } = 1.0f;

    /// <summary>True while anything is queued or playing.</summary>
    public bool IsSpeaking => Volatile.Read(ref _pending) > 0 || _playback.HasAudio;

    /// <summary>Called when the last queued sentence has finished playing.</summary>
    public event Action? Finished;

    public void Say(string text)
    {
        var clean = SpeechText.ForSpeech(text);
        if (clean.Length == 0)
        {
            return;
        }
        Interlocked.Increment(ref _pending);
        _queue.Add((clean, Interlocked.Read(ref _generation)));
    }

    /// <summary>Stops speaking now and drops everything queued.</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        _playback.Clear();
    }

    private int SpeakerId()
    {
        var i = Array.FindIndex(_voices, v => v.Equals(Voice, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i < _tts.NumSpeakers ? i : 0;
    }

    private void Work()
    {
        foreach (var (text, generation) in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (generation != Interlocked.Read(ref _generation))
                {
                    continue;
                }
                var audio = _tts.Generate(text, Rate, SpeakerId());
                var samples = audio.Samples;
                var rate = audio.SampleRate;
                audio.Dispose();
                if (generation != Interlocked.Read(ref _generation))
                {
                    continue;
                }
                _playback.Add(AudioMath.Resample(samples, rate, _playback.WaveFormat.SampleRate));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Speech failed for {Text}", text);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        while (_playback.HasAudio && Volatile.Read(ref _pending) == 0)
                        {
                            await Task.Delay(50);
                        }
                        if (Volatile.Read(ref _pending) == 0)
                        {
                            Finished?.Invoke();
                        }
                    });
                }
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        Stop();
        _output.Stop();
        _output.Dispose();
        _tts.Dispose();
    }

    /// <summary>A mono float queue played out on every channel, with the speech level for the orb.</summary>
    private sealed class FloatQueueProvider(WaveFormat format, EventHub events) : IWaveProvider
    {
        private readonly ConcurrentQueue<float[]> _chunks = new();
        private float[]? _current;
        private int _offset;
        private DateTime _lastLevel;

        public WaveFormat WaveFormat { get; } = format;

        public bool HasAudio => _current is not null || !_chunks.IsEmpty;

        public void Add(float[] samples) => _chunks.Enqueue(samples);

        public void Clear()
        {
            _chunks.Clear();
            _current = null;
        }

        public int Read(Span<byte> buffer)
        {
            var channels = WaveFormat.Channels;
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            var frames = floats.Length / channels;
            var written = 0;
            while (written < frames)
            {
                if (_current is null || _offset >= _current.Length)
                {
                    if (!_chunks.TryDequeue(out _current))
                    {
                        _current = null;
                        break;
                    }
                    _offset = 0;
                }
                var take = Math.Min(frames - written, _current.Length - _offset);
                for (var i = 0; i < take; i++)
                {
                    var s = _current[_offset + i];
                    for (var c = 0; c < channels; c++)
                    {
                        floats[(written + i) * channels + c] = s;
                    }
                }
                if (DateTime.UtcNow - _lastLevel > TimeSpan.FromMilliseconds(33))
                {
                    _lastLevel = DateTime.UtcNow;
                    events.Publish(new AudioLevel(AudioMath.Level(_current.AsSpan(_offset, take)), Speaking: true));
                }
                _offset += take;
                written += take;
            }
            // Silence for the rest: the output keeps running so the next sentence starts without a click.
            floats[(written * channels)..].Clear();
            return buffer.Length;
        }
    }
}
