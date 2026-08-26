namespace RhiGhAI.Core.Codex;

public enum RuntimeState
{
    Missing,
    Ready,
    Invalid
}

public sealed record RuntimeStatus(RuntimeState State, string Message, string? ExecutablePath);
