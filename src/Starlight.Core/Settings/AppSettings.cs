using System.Text.Json.Serialization;

namespace Starlight.Settings;

/// <summary>All of Starlight's settings. Every field has a default, so an old or missing file still loads.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public ModelSettings Model { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public WebSettings Web { get; set; } = new();
    public List<ServerEntry> Servers { get; set; } = [];

    /// <summary>Per-tool permission overrides set in Settings: tool name to Safe/Ask/Blocked.</summary>
    public Dictionary<string, Tools.Permission> PermissionOverrides { get; set; } = [];
}

public sealed class GeneralSettings
{
    public string AssistantName { get; set; } = "Starlight";
    public string UserName { get; set; } = "";
    public bool StartWithWindows { get; set; } = true;
    public string KillHotkey { get; set; } = "Ctrl+Alt+J";
    public bool GameMode { get; set; } = true;
    public List<string> GameProcesses { get; set; } = [];
}

public sealed class ModelSettings
{
    /// <summary>Folder that holds the GGUF files. Chosen on first start.</summary>
    public string ModelsDir { get; set; } = "";
    /// <summary>File name of the chat model inside ModelsDir. Any GGUF works, uncensored ones included.</summary>
    public string ChatModelFile { get; set; } = "";
    /// <summary>Optional vision projector (mmproj) for the chat model.</summary>
    public string VisionProjectorFile { get; set; } = "";
    public int ContextSize { get; set; } = 32768;
    public int GpuLayers { get; set; } = 99;
    public int Port { get; set; } = 8737;
    public double Temperature { get; set; } = 0.4;
    public int MaxSteps { get; set; } = 8;
    public string ExtraServerArgs { get; set; } = "";
    /// <summary>Use an already running OpenAI-compatible server instead of the bundled one (LM Studio, Ollama, another llama-server).</summary>
    public string ExternalEndpoint { get; set; } = "";
    public string ExternalModelName { get; set; } = "";
}

public sealed class VoiceSettings
{
    public bool Enabled { get; set; } = true;
    public string PushToTalkHotkey { get; set; } = "Ctrl+Alt+Space";
    public bool WakeWordEnabled { get; set; } = true;
    public string WakePhrase { get; set; } = "Hey Starlight";
    /// <summary>0 (strict) to 1 (eager).</summary>
    public double WakeSensitivity { get; set; } = 0.5;
    public string WhisperModelFile { get; set; } = "ggml-large-v3-turbo-q5_0.bin";
    public string Voice { get; set; } = "af_heart";
    public double SpeechRate { get; set; } = 1.0;
    public string InputDevice { get; set; } = "";
    public bool SpeakAnswers { get; set; } = true;
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Orb stays visible (dim) when idle. False hides it until called.</summary>
    public bool OrbAlwaysVisible { get; set; } = true;
    public int CaptionSeconds { get; set; } = 6;
}

public sealed class WebSettings
{
    /// <summary>"auto" uses SearXNG if set, then Brave if a key is stored, then DuckDuckGo.</summary>
    public string SearchProvider { get; set; } = "auto";
    public string SearxngUrl { get; set; } = "";
    public int MaxRequestsPerSecond { get; set; } = 5;
}

public sealed class ServerEntry
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "";
    public List<string> AllowedCommands { get; set; } = ["uptime", "df -h", "free -m", "systemctl status *", "docker ps", "journalctl -n 50 *"];
    public List<int> WatchPorts { get; set; } = [];
    public string HealthUrl { get; set; } = "";
    public int WatchMinutes { get; set; }

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name) ? Host : $"{Name} ({Host})";
}
