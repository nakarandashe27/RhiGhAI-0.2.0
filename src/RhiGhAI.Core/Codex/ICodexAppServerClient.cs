using System.Text.Json;
namespace RhiGhAI.Core.Codex;

public interface ICodexAppServerClient : IAsyncDisposable
{
    event EventHandler<CodexNotification>? NotificationReceived;
    event EventHandler? TransportClosed;
    bool IsRunning { get; }
    Task StartAsync(string executablePath, string workingDirectory, CancellationToken cancellationToken);
    Task<CodexAccountSnapshot> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken);
    Task<CodexRateLimits?> ReadRateLimitsAsync(CancellationToken cancellationToken);
    Task<CodexThread> StartThreadAsync(string model, string workingDirectory, CancellationToken cancellationToken, bool ephemeral = false);
    Task<CodexThread> ResumeThreadAsync(string threadId, string model, string workingDirectory, CancellationToken cancellationToken);
    Task<CodexTurn> StartTurnAsync(string threadId, string prompt, string model, string effort, JsonElement outputSchema, string workingDirectory, CancellationToken cancellationToken);
    Task InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken);
    Task<CodexLogin> StartChatGptLoginAsync(CancellationToken cancellationToken);
    Task LogoutAsync(CancellationToken cancellationToken);
    void Terminate();
}
