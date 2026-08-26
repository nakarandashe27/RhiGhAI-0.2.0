namespace RhiGhAI.Core.Events;

public sealed record EventEnvelope(
    int SchemaVersion,
    Guid EventId,
    Guid CorrelationId,
    string DocumentKey,
    Guid ConversationId,
    string? TurnId,
    int AttemptNo,
    string EventKind,
    DateTimeOffset Timestamp,
    string Status,
    string? ErrorCode,
    string? Message,
    string? Detail);
