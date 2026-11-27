using System.Text.Json;
using Microsoft.Data.Sqlite;
using Starlight.Storage;

namespace Starlight.Skills;

/// <summary>One step of a skill: a tool and its arguments as JSON, where {name} marks a parameter.</summary>
public sealed record SkillStep(string Tool, string ArgumentsJson);

public sealed record Skill(string Name, string Description, IReadOnlyList<SkillStep> Steps, IReadOnlyList<string> Parameters, DateTimeOffset Created);

public sealed record SkillDraft(long Id, string ConversationId, string Request, IReadOnlyList<SkillStep> Steps, IReadOnlyList<string> Parameters, DateTimeOffset Created);

/// <summary>Approved skills and offered drafts, in starlight.db.</summary>
public sealed class SkillStore
{
    private readonly StarlightDb _db;

    public SkillStore(StarlightDb db)
    {
        _db = db;
        using var c = db.Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS skills(name TEXT PRIMARY KEY, description TEXT NOT NULL, steps TEXT NOT NULL, parameters TEXT NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS skill_drafts(id INTEGER PRIMARY KEY AUTOINCREMENT, conversation TEXT NOT NULL, request TEXT NOT NULL,
              steps TEXT NOT NULL, parameters TEXT NOT NULL, created TEXT NOT NULL);
            """);
    }

    public long AddDraft(string conversation, string request, IReadOnlyList<SkillStep> steps, IReadOnlyList<string> parameters)
    {
        using var c = _db.Open();
        Exec(c, "DELETE FROM skill_drafts WHERE created < $old", ("$old", DateTimeOffset.Now.AddDays(-7).ToString("O")));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO skill_drafts(conversation,request,steps,parameters,created) VALUES($c,$r,$s,$p,$t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$c", conversation);
        cmd.Parameters.AddWithValue("$r", request);
        cmd.Parameters.AddWithValue("$s", JsonSerializer.Serialize(steps));
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(parameters));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToString("O"));
        return (long)cmd.ExecuteScalar()!;
    }

    public List<SkillDraft> Drafts(string? conversation = null)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = conversation is null
            ? "SELECT id,conversation,request,steps,parameters,created FROM skill_drafts ORDER BY id"
            : "SELECT id,conversation,request,steps,parameters,created FROM skill_drafts WHERE conversation=$c ORDER BY id";
        cmd.Parameters.AddWithValue("$c", conversation ?? "");
        using var r = cmd.ExecuteReader();
        var list = new List<SkillDraft>();
        while (r.Read())
        {
            list.Add(new SkillDraft(r.GetInt64(0), r.GetString(1), r.GetString(2), Steps(r.GetString(3)), Names(r.GetString(4)), DateTimeOffset.Parse(r.GetString(5))));
        }
        return list;
    }

    public void DropDraft(long id)
    {
        using var c = _db.Open();
        Exec(c, "DELETE FROM skill_drafts WHERE id=$id", ("$id", id));
    }

    /// <summary>The user's yes in the chat window: the draft becomes a skill under the given name.</summary>
    public Skill Approve(long draftId, string name)
    {
        var draft = Drafts().FirstOrDefault(d => d.Id == draftId) ?? throw new InvalidOperationException("The offer is gone.");
        var key = SkillName(name);
        if (Find(key) is not null)
        {
            // Never replace a skill the user already saved by accident.
            throw new InvalidOperationException($"A skill called {key} already exists. Pick another name.");
        }
        using var c = _db.Open();
        Exec(c, "INSERT INTO skills(name,description,steps,parameters,created) VALUES($n,$d,$s,$p,$t)",
            ("$n", key), ("$d", draft.Request), ("$s", JsonSerializer.Serialize(draft.Steps)), ("$p", JsonSerializer.Serialize(draft.Parameters)), ("$t", DateTimeOffset.Now.ToString("O")));
        Exec(c, "DELETE FROM skill_drafts WHERE id=$id", ("$id", draftId));
        return Find(key)!;
    }

    public List<Skill> All()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name,description,steps,parameters,created FROM skills ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<Skill>();
        while (r.Read())
        {
            list.Add(new Skill(r.GetString(0), r.GetString(1), Steps(r.GetString(2)), Names(r.GetString(3)), DateTimeOffset.Parse(r.GetString(4))));
        }
        return list;
    }

    public Skill? Find(string name)
    {
        var key = SkillName(name);
        return All().FirstOrDefault(s => s.Name == key);
    }

    public bool Delete(string name)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM skills WHERE name=$n";
        cmd.Parameters.AddWithValue("$n", SkillName(name));
        return cmd.ExecuteNonQuery() > 0;
    }

    public static string SkillName(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        var key = string.Join('_', new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return key.Length > 50 ? key[..50] : key;
    }

    private static List<SkillStep> Steps(string json) => JsonSerializer.Deserialize<List<SkillStep>>(json) ?? [];
    private static List<string> Names(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

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
}
