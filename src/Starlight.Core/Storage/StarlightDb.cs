using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Starlight.Storage;

public sealed record AuditEntry(DateTimeOffset Time, string Tool, string ArgumentsJson, string Source, string ApprovedBy, bool Ok, string Result);

public interface IAuditSink
{
    void Record(AuditEntry entry);
}

public sealed record ConversationInfo(string Id, string Title, DateTimeOffset Created, DateTimeOffset Updated, bool Pinned);

public sealed record StatRow(DateTimeOffset Time, string Kind, double Value, string? Detail);

/// <summary>One SQLite file for conversations, the audit log and statistics. Memory has its own tables (Starlight.Memory).</summary>
public sealed class StarlightDb : IAuditSink
{
    private readonly string _connectionString;

    public StarlightDb(string file)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS conversations(
              id TEXT PRIMARY KEY, title TEXT NOT NULL, created TEXT NOT NULL, updated TEXT NOT NULL,
              pinned INTEGER NOT NULL DEFAULT 0, messages TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(
              id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, tool TEXT NOT NULL, args TEXT NOT NULL,
              source TEXT NOT NULL, approved_by TEXT NOT NULL, ok INTEGER NOT NULL, result TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS stats(
              id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, kind TEXT NOT NULL, value REAL NOT NULL, detail TEXT);
            CREATE INDEX IF NOT EXISTS stats_kind_time ON stats(kind, time);
            """);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    internal static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    // ---- audit ----
    public void Record(AuditEntry e)
    {
        using var c = Open();
        Exec(c, "INSERT INTO audit(time,tool,args,source,approved_by,ok,result) VALUES($t,$tool,$a,$s,$by,$ok,$r)",
            ("$t", e.Time.ToString("O")), ("$tool", e.Tool), ("$a", e.ArgumentsJson), ("$s", e.Source),
            ("$by", e.ApprovedBy), ("$ok", e.Ok ? 1 : 0), ("$r", e.Result));
    }

    public List<AuditEntry> RecentAudit(int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time,tool,args,source,approved_by,ok,result FROM audit ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<AuditEntry>();
        while (r.Read())
        {
            list.Add(new AuditEntry(DateTimeOffset.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5) == 1, r.GetString(6)));
        }
        return list;
    }

    // ---- statistics ----
    public void Stat(string kind, double value, string? detail = null)
    {
        using var c = Open();
        Exec(c, "INSERT INTO stats(time,kind,value,detail) VALUES($t,$k,$v,$d)",
            ("$t", DateTimeOffset.Now.ToString("O")), ("$k", kind), ("$v", value), ("$d", detail));
    }

    public List<StatRow> Stats(string kind, DateTimeOffset since)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time,kind,value,detail FROM stats WHERE kind=$k AND time>=$s ORDER BY time";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$s", since.ToString("O"));
        using var r = cmd.ExecuteReader();
        var list = new List<StatRow>();
        while (r.Read())
        {
            list.Add(new StatRow(DateTimeOffset.Parse(r.GetString(0)), r.GetString(1), r.GetDouble(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return list;
    }

    // ---- conversations ----
    private static readonly JsonSerializerOptions MessageJson = AIJsonUtilities.DefaultOptions;

    public void SaveConversation(string id, string title, IList<ChatMessage> messages, DateTimeOffset created)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO conversations(id,title,created,updated,messages) VALUES($id,$title,$c,$u,$m)
            ON CONFLICT(id) DO UPDATE SET title=$title, updated=$u, messages=$m
            """,
            ("$id", id), ("$title", title), ("$c", created.ToString("O")), ("$u", DateTimeOffset.Now.ToString("O")),
            ("$m", JsonSerializer.Serialize(messages, MessageJson)));
    }

    public List<ChatMessage>? LoadMessages(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT messages FROM conversations WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<List<ChatMessage>>(json, MessageJson);
    }

    public List<ConversationInfo> ListConversations(string? search = null, int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = search is null
            ? "SELECT id,title,created,updated,pinned FROM conversations ORDER BY pinned DESC, updated DESC LIMIT $n"
            : "SELECT id,title,created,updated,pinned FROM conversations WHERE title LIKE $q OR messages LIKE $q ORDER BY pinned DESC, updated DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        if (search is not null)
        {
            cmd.Parameters.AddWithValue("$q", "%" + search + "%");
        }
        using var r = cmd.ExecuteReader();
        var list = new List<ConversationInfo>();
        while (r.Read())
        {
            list.Add(new ConversationInfo(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), DateTimeOffset.Parse(r.GetString(3)), r.GetInt32(4) == 1));
        }
        return list;
    }

    public void RenameConversation(string id, string title)
    {
        using var c = Open();
        Exec(c, "UPDATE conversations SET title=$t WHERE id=$id", ("$t", title), ("$id", id));
    }

    public void PinConversation(string id, bool pinned)
    {
        using var c = Open();
        Exec(c, "UPDATE conversations SET pinned=$p WHERE id=$id", ("$p", pinned ? 1 : 0), ("$id", id));
    }

    public void DeleteConversation(string id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM conversations WHERE id=$id", ("$id", id));
    }
}
