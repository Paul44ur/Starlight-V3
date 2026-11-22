using Starlight.Settings;

namespace Starlight.Voice;

/// <summary>Where the voice models live. Small ones ship in the app's runtime folder; whisper sits with the chat model.</summary>
public sealed class VoiceAssets(StarlightPaths paths, SettingsStore settings)
{
    public string VoiceDir => Path.Combine(paths.RuntimeDir, "voice");
    public string VadModel => Path.Combine(VoiceDir, "silero_vad.onnx");
    public string KokoroDir => Path.Combine(VoiceDir, "kokoro");
    public string KwsDir => Path.Combine(VoiceDir, "kws");

    public string WhisperModel
    {
        get
        {
            var file = settings.Current.Voice.WhisperModelFile;
            if (Path.IsPathRooted(file))
            {
                return file;
            }
            var withModels = Path.Combine(settings.Current.Model.ModelsDir, file);
            return File.Exists(withModels) ? withModels : Path.Combine(VoiceDir, file);
        }
    }

    /// <summary>What is missing, as readable lines. Empty when voice can start.</summary>
    public List<string> Missing()
    {
        var missing = new List<string>();
        if (!File.Exists(WhisperModel)) missing.Add($"speech recognition model ({WhisperModel})");
        if (!File.Exists(VadModel)) missing.Add($"voice activity model ({VadModel})");
        foreach (var f in new[] { "model.onnx", "voices.bin", "tokens.txt" })
        {
            if (!File.Exists(Path.Combine(KokoroDir, f)))
            {
                missing.Add($"voice file {f} ({KokoroDir})");
            }
        }
        return missing;
    }
}
