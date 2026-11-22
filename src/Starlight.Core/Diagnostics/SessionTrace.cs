using System.Collections.Concurrent;
using System.Text.Json;
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

    public void Write(string kind, string? turnId, object? data = null)
    {
        var entry = new TraceEntry(DateTimeOffset.Now, kind, turnId, data);
        _recent.Enqueue(entry);
        while (_recent.Count > RecentLimit && _recent.TryDequeue(out _)) { }
        var line = JsonSerializer.Serialize(entry, Json);
        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_dir, $"trace-{DateTime.Now:yyyyMMdd}.jsonl"), line + Environment.NewLine);
            }
            catch (IOException)
            {
                // A trace line is never worth failing a turn for.
            }
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
        foreach (var f in Directory.EnumerateFiles(_dir, "trace-*.jsonl"))
        {
            if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-days))
            {
                try { File.Delete(f); } catch (IOException) { }
            }
        }
    }
}

public sealed record TraceEntry(DateTimeOffset Time, string Kind, string? Turn, object? Data);
