using System.Globalization;
using Serilog;
using SherpaOnnx;

namespace Starlight.Voice;

/// <summary>
/// Always-on wake phrase (plan P6): sherpa-onnx open-vocabulary keyword spotting on the CPU. Any phrase
/// typed in Settings works without training. Model files: kws/encoder*.onnx, decoder*.onnx, joiner*.onnx, tokens.txt.
/// </summary>
public sealed class WakeWord : IDisposable
{
    private readonly KeywordSpotter _spotter;
    private OnlineStream _stream;
    private readonly Lock _gate = new();

    public WakeWord(string kwsDir, string phrase, double sensitivity)
    {
        string Find(string prefix) =>
            Directory.GetFiles(kwsDir, prefix + "*.onnx").OrderBy(f => f.Contains("int8", StringComparison.Ordinal) ? 0 : 1).FirstOrDefault()
            ?? throw new FileNotFoundException($"No {prefix}*.onnx in {kwsDir}");
        var tokens = Path.Combine(kwsDir, "tokens.txt");
        var line = WakeTokens.Line(phrase, WakeTokens.ReadVocab(File.ReadLines(tokens)), threshold: WakeTokens.Threshold(sensitivity))
            ?? throw new ArgumentException($"The wake phrase \"{phrase}\" can't be spotted with this model. Use plain English words.");
        var config = new KeywordSpotterConfig();
        config.FeatConfig.SampleRate = Microphone.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Find("encoder");
        config.ModelConfig.Transducer.Decoder = Find("decoder");
        config.ModelConfig.Transducer.Joiner = Find("joiner");
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        config.KeywordsScore = 1.5f;
        config.KeywordsThreshold = (float)WakeTokens.Threshold(sensitivity);
        config.KeywordsBuf = line;
        config.KeywordsBufSize = System.Text.Encoding.UTF8.GetByteCount(line);
        config.MaxActivePaths = 4;
        config.NumTrailingBlanks = 1;
        _spotter = new KeywordSpotter(config);
        _stream = _spotter.CreateStream();
        Log.Information("Wake phrase \"{Phrase}\" ready ({Tokens}), threshold {Threshold}", phrase, line, config.KeywordsThreshold.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Raised on the audio thread when the phrase is heard.</summary>
    public event Action? Heard;

    /// <summary>When false (Starlight is speaking or already listening), audio is ignored.</summary>
    public bool Active { get; set; } = true;

    public void Feed(float[] chunk)
    {
        if (!Active)
        {
            return;
        }
        var heard = false;
        lock (_gate)
        {
            _stream.AcceptWaveform(Microphone.SampleRate, chunk);
            while (_spotter.IsReady(_stream))
            {
                _spotter.Decode(_stream);
                if (!string.IsNullOrEmpty(_spotter.GetResult(_stream).Keyword))
                {
                    heard = true;
                    _spotter.Reset(_stream);
                }
            }
        }
        if (heard)
        {
            Log.Information("Wake phrase heard");
            Heard?.Invoke();
        }
    }

    /// <summary>Forgets audio so far (after Starlight spoke, so its own voice can't wake it).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _stream.Dispose();
            _stream = _spotter.CreateStream();
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _spotter.Dispose();
    }
}
