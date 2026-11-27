namespace Starlight.Tools;

/// <summary>A question to the user. <paramref name="CanRemember"/> is false when "Always" would not be
/// stored (only some arguments ask, or outside text is in the turn), so the button is not offered.</summary>
public sealed record ApprovalRequest(string Tool, string Question, string ArgumentsJson, string Source, bool CanRemember = true);

public enum ApprovalAnswer { No, Yes, Always }

/// <summary>Asks the user before an Ask tool runs. The overlay card, voice and the chat window answer it.</summary>
public interface IApprovalService
{
    Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>Says no to everything. Used when no user is there to ask (tests, eval).</summary>
public sealed class DenyAllApprovals : IApprovalService
{
    public Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult(ApprovalAnswer.No);
}

/// <summary>Asks the user to type or say a short text, such as a two-factor code. Null means no answer.</summary>
public interface IUserPrompt
{
    Task<string?> AskTextAsync(string title, string question, CancellationToken ct);
}

/// <summary>No user to ask (tests, eval, the console without a window).</summary>
public sealed class NoUserPrompt : IUserPrompt
{
    public Task<string?> AskTextAsync(string title, string question, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>Info about the tool call in progress, readable from inside a tool.</summary>
public sealed record ToolCallContext(string TurnId, string Source, string ToolName)
{
    private static readonly AsyncLocal<ToolCallContext?> s_current = new();

    public static ToolCallContext? Current
    {
        get => s_current.Value;
        internal set => s_current.Value = value;
    }
}
