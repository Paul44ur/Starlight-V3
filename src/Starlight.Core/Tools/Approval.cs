namespace Starlight.Tools;

public sealed record ApprovalRequest(string Tool, string Question, string ArgumentsJson, string Source);

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
