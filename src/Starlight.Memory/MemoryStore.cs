using System.Text;
using Microsoft.Data.Sqlite;
using Starlight.Storage;

namespace Starlight.Memory;

public sealed record Fact(long Id, string Text, string Topic, string? Conversation, DateTimeOffset Created, DateTimeOffset Updated);

/// <summary>
/// What Starlight remembers (plan 5.7), in starlight.db: one row per fact with a topic. A new fact on a topic
/// replaces the old one, which stays in the history table. Full-text search (FTS5) finds facts for a prompt.
/// </summary>
public sealed class MemoryStore
{
    private readonly StarlightDb _db;

    public MemoryStore(StarlightDb db)
    {
        _db = db;
        using var c = db.Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS facts(
              id INTEGER PRIMARY KEY AUTOINCREMENT, text TEXT NOT NULL, topic TEXT NOT NULL,
              conversation TEXT, created TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS facts_topic ON facts(topic);
            CREATE TABLE IF NOT EXISTS facts_history(
              id INTEGER PRIMARY KEY AUTOINCREMENT, fact_id INTEGER, text TEXT NOT NULL, topic TEXT NOT NULL,
              conversation TEXT, changed TEXT NOT NULL, reason TEXT NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS facts_fts USING fts5(text, topic, content='facts', content_rowid='id', tokenize='porter unicode61');
            CREATE TRIGGER IF NOT EXISTS facts_ai AFTER INSERT ON facts BEGIN
              INSERT INTO facts_fts(rowid, text, topic) VALUES (new.id, new.text, new.topic); END;
            CREATE TRIGGER IF NOT EXISTS facts_ad AFTER DELETE ON facts BEGIN
              INSERT INTO facts_fts(facts_fts, rowid, text, topic) VALUES ('delete', old.id, old.text, old.topic); END;
            CREATE TRIGGER IF NOT EXISTS facts_au AFTER UPDATE ON facts BEGIN
              INSERT INTO facts_fts(facts_fts, rowid, text, topic) VALUES ('delete', old.id, old.text, old.topic);
              INSERT INTO facts_fts(rowid, text, topic) VALUES (new.id, new.text, new.topic); END;
            """);
    }

    /// <summary>Saves a fact. Returns the fact it replaced on the same topic, if any.</summary>
    public (Fact Saved, Fact? Replaced) Remember(string text, string? topic, string? conversation)
    {
        text = Clean(text);
        // Without a topic the whole fact is the key, so two different facts never replace each other.
        var key = string.IsNullOrWhiteSpace(topic) ? NormalizeTopic(text, 40) : NormalizeTopic(topic);
        if (key.Length == 0)
        {
            key = text.ToLowerInvariant();
        }
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var old = Query(c, "SELECT id,text,topic,conversation,created,updated FROM facts WHERE topic=$t", ("$t", key)).FirstOrDefault();
        var now = DateTimeOffset.Now.ToString("O");
        if (old is not null)
        {
            Exec(c, "INSERT INTO facts_history(fact_id,text,topic,conversation,changed,reason) VALUES($id,$x,$t,$c,$n,'replaced')",
                ("$id", old.Id), ("$x", old.Text), ("$t", old.Topic), ("$c", old.Conversation), ("$n", now));
            Exec(c, "UPDATE facts SET text=$x, conversation=$c, updated=$n WHERE id=$id", ("$x", text), ("$c", conversation), ("$n", now), ("$id", old.Id));
        }
        else
        {
            Exec(c, "INSERT INTO facts(text,topic,conversation,created,updated) VALUES($x,$t,$c,$n,$n)", ("$x", text), ("$t", key), ("$c", conversation), ("$n", now));
        }
        tx.Commit();
        var saved = Query(c, "SELECT id,text,topic,conversation,created,updated FROM facts WHERE topic=$t", ("$t", key)).Single();
        return (saved, old);
    }

    /// <summary>The facts that best match the words, best first.</summary>
    public List<Fact> Search(string query, int limit = 5)
    {
        var match = FtsQuery(query);
        if (match is null)
        {
            return [];
        }
        using var c = _db.Open();
        return Query(c, """
            SELECT f.id,f.text,f.topic,f.conversation,f.created,f.updated FROM facts_fts
            JOIN facts f ON f.id = facts_fts.rowid WHERE facts_fts MATCH $q ORDER BY bm25(facts_fts) LIMIT $n
            """, ("$q", match), ("$n", limit));
    }

    public List<Fact> All(string? filter = null)
    {
        using var c = _db.Open();
        return string.IsNullOrWhiteSpace(filter)
            ? Query(c, "SELECT id,text,topic,conversation,created,updated FROM facts ORDER BY updated DESC")
            : Query(c, "SELECT id,text,topic,conversation,created,updated FROM facts WHERE text LIKE $q OR topic LIKE $q ORDER BY updated DESC", ("$q", "%" + filter + "%"));
    }

    /// <summary>Forgets a fact for good: its earlier versions in the history go too.</summary>
    public bool Forget(long id)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var f = Query(c, "SELECT id,text,topic,conversation,created,updated FROM facts WHERE id=$id", ("$id", id)).FirstOrDefault();
        if (f is null)
        {
            return false;
        }
        Exec(c, "DELETE FROM facts_history WHERE fact_id=$id", ("$id", id));
        Exec(c, "DELETE FROM facts WHERE id=$id", ("$id", id));
        tx.Commit();
        return true;
    }

    /// <summary>Earlier versions of a fact (replaced texts), newest first.</summary>
    public List<string> History(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT text FROM facts_history WHERE fact_id=$id ORDER BY id DESC";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
        {
            list.Add(r.GetString(0));
        }
        return list;
    }

    public const int MaxFactLength = 300;

    /// <summary>One line, at most <see cref="MaxFactLength"/> characters: a fact goes into every matching prompt.</summary>
    internal static string Clean(string text)
    {
        var one = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length > MaxFactLength ? one[..MaxFactLength].TrimEnd() + "…" : one;
    }

    /// <summary>"Forget this conversation": removes what was learned in it.</summary>
    public int ForgetConversation(string conversationId)
    {
        var facts = All().Where(f => f.Conversation == conversationId).ToList();
        foreach (var f in facts)
        {
            Forget(f.Id);
        }
        return facts.Count;
    }

    internal static string NormalizeTopic(string topic, int maxWords = 6)
    {
        var words = new string(topic.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).Take(maxWords);
        return string.Join(' ', words);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "to", "of", "and", "or", "in", "on", "at", "for", "with", "my", "me", "i",
        "you", "your", "it", "this", "that", "what", "who", "when", "where", "how", "do", "does", "did", "can", "could", "please", "about", "tell", "know",
    };

    /// <summary>Words of the request as an FTS5 OR query, or null if nothing is left to search for.</summary>
    internal static string? FtsQuery(string text)
    {
        var words = new string(text.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .Select(w => "\"" + w.ToLowerInvariant() + "\"*")
            .Distinct()
            .Take(12)
            .ToList();
        return words.Count == 0 ? null : string.Join(" OR ", words);
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    private static List<Fact> Query(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }
        using var r = cmd.ExecuteReader();
        var list = new List<Fact>();
        while (r.Read())
        {
            list.Add(new Fact(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                DateTimeOffset.Parse(r.GetString(4)), DateTimeOffset.Parse(r.GetString(5))));
        }
        return list;
    }
}
