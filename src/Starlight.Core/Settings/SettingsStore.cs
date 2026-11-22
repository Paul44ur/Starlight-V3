using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Starlight.Settings;

/// <summary>Loads and saves settings.json. A broken file is kept aside and replaced by defaults, never lost.</summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            var aside = _path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(_path, aside, overwrite: true); } catch (IOException) { }
            Log.Error(ex, "Settings file could not be read; kept it as {Aside} and started with defaults", aside);
            return new AppSettings();
        }
    }

    /// <summary>Changes settings and writes them atomically (temp file, then replace, keeping a .bak).</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            Save(Current);
        }
        Changed?.Invoke(Current);
    }

    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        if (File.Exists(_path))
        {
            File.Replace(tmp, _path, _path + ".bak");
        }
        else
        {
            File.Move(tmp, _path);
        }
    }

    /// <summary>The settings as JSON for the diagnostics file. Holds no secrets: those live in Credential Manager.</summary>
    public string ToJson() => JsonSerializer.Serialize(Current, Json);
}
