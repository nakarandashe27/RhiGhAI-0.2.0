using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RhiGhAI.Core.Codex;

namespace RhiGhAI.Core.Providers;

/// <summary>
/// Plans through the managed Codex runtime and the shared ChatGPT login: no API key, no billing setup.
/// Everything Codex-specific — runtime staging, login, threads, forbidden tool items — lives here.
/// </summary>
public sealed class CodexPlanProvider : IPlanProvider
{
    private readonly CodexRuntimeManager _runtime = new();
    private readonly ConcurrentDictionary<string, CompletedTurn> _earlyCompletedTurns = new(StringComparer.Ordinal);
    private CodexAppServerClient? _client;
    private TaskCompletionSource<CompletedTurn>? _turnCompletion;
    private volatile string? _lastAgentMessage;
    private string? _activeTurnId;
    private bool _threadNeedsResume;

    public ProviderKind Kind => ProviderKind.Codex;

    /// <summary>Codex thread bound to the current document; persisted by the caller.</summary>
    public string? ThreadId { get; set; }

    public RuntimeStatus Runtime { get; private set; } = new(RuntimeState.Missing, "Codex runtime ещё не проверен.", null);

    public CodexAccountSnapshot? Account { get; private set; }

    public CodexRateLimits? RateLimits { get; private set; }

    public async Task<ProviderStatus> ConnectAsync(CancellationToken cancellationToken)
    {
        RuntimeStatus runtime = _runtime.Inspect();
        if (runtime.State != RuntimeState.Ready)
        {
            runtime = await _runtime.PrepareAsync(null, cancellationToken).ConfigureAwait(false);
        }

        Runtime = runtime;
        if (runtime.State != RuntimeState.Ready || runtime.ExecutablePath is null)
        {
            Account = null;
            RateLimits = null;
            return new ProviderStatus(false, runtime.Message, "Codex не найден", null, []);
        }

        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        Account = await _client!.ReadAccountAsync(false, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CodexModel> models = await _client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        RateLimits = Account.Account is null ? null : await ReadRateLimitsSafeAsync(cancellationToken).ConfigureAwait(false);

        string account = Account.Account is { } item ? $"{item.Email ?? item.Type} · {item.PlanType ?? "ChatGPT"}" : "вход не выполнен";
        string usage = RateLimits?.Primary is { } primary ? $"{primary.UsedPercent:0}%" : "—";
        return new ProviderStatus(
            Account.Account is not null,
            $"{runtime.Message}\nАккаунт: {account}",
            account,
            usage,
            [.. models.Select(model => new ProviderModel(
                model.Id,
                model.DisplayName,
                [.. model.SupportedReasoningEfforts.Select(effort => effort.Id)],
                model.DefaultReasoningEffort,
                model.IsDefault))]);
    }

    public async Task<string> RequestJsonAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        if (Account?.Account is null)
        {
            throw new ProviderException("CodexLoginRequired", "Требуется вход в аккаунт ChatGPT.");
        }

        if (ThreadId is null)
        {
            CodexThread thread = await _client!.StartThreadAsync(request.Model, _runtime.EmptyWorkingDirectory, cancellationToken).ConfigureAwait(false);
            ThreadId = thread.Id;
            _threadNeedsResume = false;
        }
        else if (_threadNeedsResume)
        {
            CodexThread thread = await _client!.ResumeThreadAsync(ThreadId, request.Model, _runtime.EmptyWorkingDirectory, cancellationToken).ConfigureAwait(false);
            ThreadId = thread.Id;
            _threadNeedsResume = false;
        }

        TaskCompletionSource<CompletedTurn> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastAgentMessage = null;
        _turnCompletion = completion;
        CodexTurn turn = await _client!.StartTurnAsync(
            ThreadId!,
            request.Prompt,
            request.Model,
            request.Effort,
            request.OutputSchema,
            _runtime.EmptyWorkingDirectory,
            cancellationToken).ConfigureAwait(false);
        _activeTurnId = turn.Id;
        if (_earlyCompletedTurns.TryRemove(turn.Id, out CompletedTurn? early))
        {
            completion.TrySetResult(early);
        }

        CompletedTurn completed;
        try
        {
            completed = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Cancellation, a timeout and ForbiddenToolItem all leave through here. Without the reset
            // Interrupt() would keep addressing a dead turn id for the rest of the session.
            _turnCompletion = null;
            _activeTurnId = null;
        }

        if (!string.Equals(completed.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new CodexProtocolException(completed.Error ?? $"Codex turn завершён со статусом {completed.Status}.");
        }

        if (string.IsNullOrWhiteSpace(completed.Text))
        {
            throw new CodexProtocolException("Codex не вернул структурированный ответ.");
        }

        return completed.Text;
    }

    public void ResetConversation()
    {
        ThreadId = null;
        _threadNeedsResume = false;
    }

    public void Interrupt()
    {
        string? threadId = ThreadId;
        string? turnId = _activeTurnId;
        if (_client is not null && threadId is not null && turnId is not null)
        {
            _ = InterruptSafeAsync(_client, threadId, turnId);
        }
    }

    public async Task PrepareRuntimeAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Runtime = await _runtime.PrepareAsync(progress, cancellationToken).ConfigureAwait(false);
        if (Runtime.State != RuntimeState.Ready)
        {
            throw new FileNotFoundException(Runtime.Message);
        }
    }

    public async Task<Uri> LoginAsync(CancellationToken cancellationToken)
    {
        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        CodexLogin login = await _client!.StartChatGptLoginAsync(cancellationToken).ConfigureAwait(false);
        Process.Start(new ProcessStartInfo(login.AuthUrl.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return login.AuthUrl;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return;
        }

        await _client.LogoutAsync(cancellationToken).ConfigureAwait(false);
        Account = null;
    }

    public void Terminate() => _client?.Terminate();

    public async ValueTask DisposeAsync()
    {
        Interrupt();
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
    }

    private async Task EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsRunning: true })
        {
            return;
        }

        RuntimeStatus runtime = _runtime.Inspect();
        Runtime = runtime;
        if (runtime.State != RuntimeState.Ready || runtime.ExecutablePath is null)
        {
            throw new FileNotFoundException("Codex runtime не найден. Откройте настройки и нажмите «Подключить Codex».");
        }

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _client = new CodexAppServerClient();
        _client.NotificationReceived += OnCodexNotification;
        await _client.StartAsync(runtime.ExecutablePath, _runtime.EmptyWorkingDirectory, cancellationToken).ConfigureAwait(false);
        _threadNeedsResume = ThreadId is not null;
    }

    private async Task<CodexRateLimits?> ReadRateLimitsSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _client!.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CodexProtocolException)
        {
            return null;
        }
    }

    private static async Task InterruptSafeAsync(CodexAppServerClient client, string threadId, string turnId)
    {
        try
        {
            await client.InterruptTurnAsync(threadId, turnId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CodexProtocolException or InvalidOperationException or IOException or OperationCanceledException)
        {
            // Cancellation must remain safe even if app-server has already exited.
        }
    }

    private void OnCodexNotification(object? sender, CodexNotification notification)
    {
        if (notification.Parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (notification.Method is "item/started" or "item/completed" &&
            notification.Parameters.TryGetProperty("item", out JsonElement item) &&
            item.ValueKind == JsonValueKind.Object)
        {
            string type = Text(item, "type") ?? string.Empty;
            if (type is not ("userMessage" or "agentMessage" or "reasoning"))
            {
                _turnCompletion?.TrySetException(
                    new ProviderException("ForbiddenToolItem", $"Codex попытался использовать запрещённый item: {type}."));
                Interrupt();
            }
            else if (type == "agentMessage" && notification.Method == "item/completed" && Text(item, "text") is { Length: > 0 } itemText)
            {
                // turn/completed may arrive with itemsView "notLoaded"; the item event is the reliable source.
                _lastAgentMessage = itemText;
            }
        }

        if (notification.Method != "turn/completed" ||
            !notification.Parameters.TryGetProperty("turn", out JsonElement turn) ||
            turn.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string id = Text(turn, "id") ?? string.Empty;
        string status = Text(turn, "status") ?? "failed";
        string? text = null;
        if (turn.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement completedItem in items.EnumerateArray())
            {
                if (completedItem.ValueKind == JsonValueKind.Object && Text(completedItem, "type") == "agentMessage")
                {
                    text = Text(completedItem, "text") ?? text;
                }
            }
        }

        text ??= _lastAgentMessage;
        _lastAgentMessage = null;
        string? error = turn.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind != JsonValueKind.Null
            ? Text(errorElement, "message") ?? errorElement.GetRawText()
            : null;
        CompletedTurn completed = new(id, status, text, error);
        if (string.Equals(id, _activeTurnId, StringComparison.Ordinal))
        {
            _turnCompletion?.TrySetResult(completed);
        }
        else
        {
            // Only the turn currently starting can still claim one of these. Interrupted turns are
            // never claimed, so the dictionary is bounded rather than left to grow all session.
            if (_earlyCompletedTurns.Count >= 16)
            {
                _earlyCompletedTurns.Clear();
            }

            _earlyCompletedTurns[id] = completed;
        }
    }

    private static string? Text(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record CompletedTurn(string Id, string Status, string? Text, string? Error);
}
