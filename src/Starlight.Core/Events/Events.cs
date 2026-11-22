namespace Starlight.Events;

public abstract record StarlightEvent;

public enum AssistantState { Idle, Listening, Thinking, Acting, Speaking, Asking, Paused, Killed }

/// <summary>What the assistant is doing now; drives the orb.</summary>
public sealed record StateChanged(AssistantState State, string? Detail = null) : StarlightEvent;

/// <summary>Microphone level 0..1 while listening, speech envelope 0..1 while speaking.</summary>
public sealed record AudioLevel(double Level, bool Speaking) : StarlightEvent;

/// <summary>What the user said (final transcript).</summary>
public sealed record UserSaid(string Text, string Source) : StarlightEvent;

public sealed record TurnStarted(string TurnId, string ConversationId, string Text, string Source) : StarlightEvent;
public sealed record AnswerDelta(string TurnId, string Delta) : StarlightEvent;
public sealed record AnswerCompleted(string TurnId, string Text, bool Failed) : StarlightEvent;
public sealed record ToolStarted(string TurnId, string Tool, string ArgumentsJson) : StarlightEvent;
public sealed record ToolFinished(string TurnId, string Tool, bool Ok, string Summary, TimeSpan Took) : StarlightEvent;

/// <summary>A rectangle on screen in physical pixels, virtual-desktop coordinates.</summary>
public sealed record ScreenRect(int X, int Y, int Width, int Height)
{
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
}

/// <summary>Draw the pointer from the orb to a target. Exact = found by UI Automation; otherwise a guess (dashed).</summary>
public sealed record PointAt(ScreenRect Target, string Label, bool Exact, TimeSpan Duration) : StarlightEvent;
public sealed record ClearPointer : StarlightEvent;

/// <summary>Starlight is controlling mouse and keyboard; the overlay draws the red frame.</summary>
public sealed record ControlActive(bool Active) : StarlightEvent;

public sealed record KillSwitchChanged(bool Engaged, string Reason) : StarlightEvent;
public sealed record Notice(string Text, NoticeLevel Level = NoticeLevel.Info) : StarlightEvent;
public enum NoticeLevel { Info, Warning, Error }

/// <summary>Model server status for the tray and the chat window.</summary>
public sealed record ModelStatus(string Status, string? Detail = null) : StarlightEvent;

/// <summary>Starlight offers to save the steps of a turn as a skill; the chat window asks the user.</summary>
public sealed record SkillOffered(long DraftId, string ConversationId, string Name) : StarlightEvent;
