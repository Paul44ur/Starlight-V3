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
        if (TryRead(_path, out var settings, out var error))
        {
            return settings;
        }
        var aside = _path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            File.Copy(_path, aside, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        // One bad field must not lose everything: the copy from the last good save comes next.
        if (TryRead(_path + ".bak", out var backup, out _))
        {
            Log.Error(error, "Settings file could not be read; kept it as {Aside} and used the backup", aside);
            return backup;
        }
        Log.Error(error, "Settings file could not be read; kept it as {Aside} and started with defaults", aside);
        return new AppSettings();
    }

    private static bool TryRead(string path, out AppSettings settings, out Exception? error)
    {
        settings = new AppSettings();
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            var read = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
            if (read is null)
            {
                error = new JsonException("The settings file is empty.");
                return false;
            }
            settings = read;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>Changes settings and writes them atomically (temp file, then replace, keeping a .bak).
    /// The change is made on a copy: if saving fails, the live settings stay as they were, and a reader
    /// never sees a half-made change.</summary>
    public void Update(Action<AppSettings> change)
    {
        AppSettings next;
        lock (_gate)
        {
            next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Current, Json), Json)!;
            change(next);
            Save(next);
            Current = next;
        }
        Changed?.Invoke(next);
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
