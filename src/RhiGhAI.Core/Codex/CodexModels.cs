using System.Text.Json;

namespace RhiGhAI.Core.Codex;

public sealed record CodexAccount(string Type, string? Email, string? PlanType);
public sealed record CodexAccountSnapshot(CodexAccount? Account, bool RequiresOpenAiAuth);
public sealed record CodexRateLimitWindow(double UsedPercent, long? ResetsAt);
public sealed record CodexRateLimits(CodexRateLimitWindow? Primary, CodexRateLimitWindow? Secondary, string? ReachedType);
public sealed record CodexReasoningEffort(string Id, string Description);
public sealed record CodexModel(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<CodexReasoningEffort> SupportedReasoningEfforts,
    string DefaultReasoningEffort,
    bool IsDefault);
public sealed record CodexLogin(string LoginId, Uri AuthUrl);
public sealed record CodexThread(string Id);
public sealed record CodexTurn(string Id, string Status);
public sealed record CodexNotification(string Method, JsonElement Parameters);
