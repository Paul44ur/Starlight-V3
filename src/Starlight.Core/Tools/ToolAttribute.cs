namespace Starlight.Tools;

public enum Permission
{
    /// <summary>Runs without asking.</summary>
    Safe,
    /// <summary>Asks first: Yes/No card on the overlay, or by voice.</summary>
    Ask,
    /// <summary>Never runs.</summary>
    Blocked,
}

/// <summary>Marks a public method of an <see cref="IToolProvider"/> as a tool the model can call.
/// Describe the tool and each parameter with [Description]; that text is what the model reads.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ToolAttribute(string name, Permission permission = Permission.Safe) : Attribute
{
    public string Name { get; } = name;
    public Permission Permission { get; } = permission;
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>For a Safe tool with an <c>action</c> parameter: the actions that ask first anyway
    /// (for example <c>app</c> opens without asking but asks before closing).</summary>
    public string[] AskForActions { get; init; } = [];
}

/// <summary>A group of tools. Providers never reference each other; they call other tools through <see cref="IToolCaller"/>.</summary>
public interface IToolProvider
{
}

/// <summary>Lets a tool call another tool through the dispatcher (so permissions and the audit still apply).</summary>
public interface IToolCaller
{
    Task<ToolResult> CallAsync(string tool, IDictionary<string, object?> arguments, CancellationToken ct);
}

/// <summary>What a tool returns when it has more than text: images (a screenshot) go to the model as a picture.</summary>
public sealed record ToolOutput(string Text, IReadOnlyList<Microsoft.Extensions.AI.DataContent> Images);

public sealed record ToolResult(bool Ok, string Text)
{
    /// <summary>Pictures for the model (only with a vision model).</summary>
    public IReadOnlyList<Microsoft.Extensions.AI.DataContent> Images { get; init; } = [];

    public static ToolResult Success(string text) => new(true, text);
    public static ToolResult Fail(string text) => new(false, text);
}

/// <summary>Raised by a tool for an expected failure the model should read (no stack trace in the error log).</summary>
public sealed class ToolException(string message) : Exception(message);
