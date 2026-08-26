using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhiGhAI.Core.Providers;

/// <summary>Where plans come from: the managed Codex runtime or any OpenAI-compatible HTTP API.</summary>
// The converter also reads the plain numbers written by settings.json before 0.2.1.
[JsonConverter(typeof(JsonStringEnumConverter<ProviderKind>))]
public enum ProviderKind
{
    Codex,
    OpenAiCompatible
}

public sealed record ProviderModel(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Efforts,
    string DefaultEffort,
    bool IsDefault);

public sealed record ProviderStatus(
    bool Ready,
    string Message,
    string AccountText,
    string? UsageText,
    IReadOnlyList<ProviderModel> Models);

/// <summary>
/// One structured-output request: the whole prompt plus the schema the answer must match.
/// <paramref name="Stateless"/> marks a prompt that already carries everything it needs, so the
/// provider neither sends nor stores conversation history for it.
/// </summary>
public sealed record PlanRequest(
    string SchemaName,
    JsonElement OutputSchema,
    string Prompt,
    string Model,
    string Effort,
    bool Stateless = false);

public interface IPlanProvider : IAsyncDisposable
{
    ProviderKind Kind { get; }

    /// <summary>Checks availability and returns the model list; never throws for a merely unconfigured provider.</summary>
    Task<ProviderStatus> ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Returns raw JSON matching <see cref="PlanRequest.OutputSchema"/>.</summary>
    Task<string> RequestJsonAsync(PlanRequest request, CancellationToken cancellationToken);

    /// <summary>Drops any server-side or in-memory conversation history.</summary>
    void ResetConversation();

    /// <summary>Best-effort interrupt of an in-flight request.</summary>
    void Interrupt();
}

public sealed class ProviderException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
