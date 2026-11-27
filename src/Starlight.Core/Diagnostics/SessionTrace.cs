using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Starlight.Diagnostics;

/// <summary>One line per event of a turn (heard, tool called, answer, timings), in trace/trace-yyyyMMdd.jsonl.
/// Lets a failure from a bulk test be found and replayed in the eval.</summary>
public sealed class SessionTrace
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _dir;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<TraceEntry> _recent = new();
    private const int RecentLimit = 500;

    public SessionTrace(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Writes one event. Every string in <paramref name="data"/> goes through <see cref="SecretMasker"/>
    /// first, because the trace ships in the diagnostics zip. A trace line never fails a turn.
    /// </summary>
    public void Write(string kind, string? turnId, object? data = null)
    {
        try
        {
            var entry = new TraceEntry(DateTimeOffset.Now, kind, turnId, MaskData(data));
            _recent.Enqueue(entry);
            while (_recent.Count > RecentLimit && _recent.TryDequeue(out _)) { }
            var line = JsonSerializer.Serialize(entry, Json);
            lock (_gate)
            {
                File.AppendAllText(Path.Combine(_dir, $"trace-{DateTime.Now:yyyyMMdd}.jsonl"), line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            // A trace line is never worth failing a turn for.
        }
    }

    /// <summary>Turns the data into JSON and masks every string value and every secret-looking field.</summary>
    internal static JsonNode? MaskData(object? data)
    {
        if (data is null)
        {
            return null;
        }
        var node = data as JsonNode ?? JsonSerializer.SerializeToNode(data, Json);
        return Mask(node);
    }

    private static readonly string[] SecretNames = ["password", "passwd", "secret", "token", "apikey", "api_key", "cookie"];

    private static JsonNode? Mask(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    o[key] = SecretNames.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase)) && value is JsonValue
                        ? JsonValue.Create("***")
                        : Mask(value?.DeepClone());
                }
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    a[i] = Mask(a[i]?.DeepClone());
                }
                return a;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(SecretMasker.Mask(s));
            default:
                return node;
        }
    }

    /// <summary>Entries from the last <paramref name="window"/>, for "Report a problem".</summary>
    public IReadOnlyList<TraceEntry> Recent(TimeSpan window)
    {
        var since = DateTimeOffset.Now - window;
        return _recent.Where(e => e.Time >= since).ToList();
    }

    /// <summary>Deletes trace files older than <paramref name="days"/>.</summary>
    public void Prune(int days = 30)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "trace-*.jsonl"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-days))
                {
                    try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed record TraceEntry(DateTimeOffset Time, string Kind, string? Turn, object? Data);
