using NAudio.CoreAudioApi;
using Serilog;
using Starlight.Events;

namespace Starlight.Voice;

/// <summary>Records the microphone as 16 kHz mono floats (what whisper and the VAD want) and publishes the level for the orb.</summary>
public sealed class Microphone : IDisposable
{
    public const int SampleRate = 16000;
    private readonly EventHub _events;
    private readonly string _deviceName;
    private readonly Lock _gate = new();
    private WasapiCapture? _capture;
    private List<float>? _recording;
    private DateTime _lastLevel;

    public Microphone(EventHub events, string deviceName = "")
    {
        _events = events;
        _deviceName = deviceName;
    }

    /// <summary>Every 16 kHz chunk as it arrives (for the wake phrase and hands-free listening).</summary>
    public event Action<float[]>? Samples;

    public bool IsRecording => _recording is not null;

    /// <summary>Starts capturing (idempotent). Samples flow to <see cref="Samples"/>.</summary>
    public void Open()
    {
        lock (_gate)
        {
            if (_capture is not null)
            {
                return;
            }
            var device = FindDevice();
            _capture = new WasapiCapture(device, true, 50);
            var format = _capture.WaveFormat;
            Log.Information("Microphone: {Device}, {Rate} Hz, {Channels} ch, {Bits} bit {Encoding}", device.FriendlyName, format.SampleRate, format.Channels, format.BitsPerSample, format.Encoding);
            var isFloat = format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat
                || (format.Encoding == NAudio.Wave.WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
            _capture.DataAvailable += (_, e) =>
            {
                var mono = AudioMath.ToMono(e.BufferSpan[..e.BytesRecorded], format.Channels, isFloat, format.BitsPerSample);
                var chunk = AudioMath.Resample(mono, format.SampleRate, SampleRate);
                OnChunk(chunk);
            };
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    Log.Error(e.Exception, "Microphone stopped with an error");
                    _events.Publish(new Notice("The microphone stopped working. Check it in Windows' sound settings.", NoticeLevel.Error));
                }
            };
            _capture.StartRecording();
        }
    }

    private MMDevice FindDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(_deviceName))
        {
            var named = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains(_deviceName, StringComparison.OrdinalIgnoreCase));
            if (named is not null)
            {
                return named;
            }
            Log.Warning("Microphone {Name} not found; using the default", _deviceName);
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    }

    private void OnChunk(float[] chunk)
    {
        lock (_gate)
        {
            _recording?.AddRange(chunk);
        }
        Samples?.Invoke(chunk);
        if (_recording is not null && DateTime.UtcNow - _lastLevel > TimeSpan.FromMilliseconds(33))
        {
            _lastLevel = DateTime.UtcNow;
            _events.Publish(new AudioLevel(AudioMath.Level(chunk), Speaking: false));
        }
    }

    /// <summary>Starts keeping audio for one utterance.</summary>
    public void BeginUtterance()
    {
        Open();
        lock (_gate)
        {
            _recording = new List<float>(SampleRate * 10);
        }
    }

    /// <summary>Stops keeping audio and returns what was said.</summary>
    public float[] EndUtterance()
    {
        lock (_gate)
        {
            var audio = _recording?.ToArray() ?? [];
            _recording = null;
            return audio;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Dispose() => Close();
}
