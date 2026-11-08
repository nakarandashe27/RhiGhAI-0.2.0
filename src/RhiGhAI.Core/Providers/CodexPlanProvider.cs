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
    private readonly CodexRuntimeManager _runtime;
    private readonly Func<ICodexAppServerClient> _createClient;
    private ICodexAppServerClient? _client;
    private string? _activeTurnId;
    private string? _activeThreadId;
    private bool _threadNeedsResume;

    public CodexPlanProvider() : this(new CodexRuntimeManager(), () => new CodexAppServerClient()) { }
    internal CodexPlanProvider(CodexRuntimeManager runtime, Func<ICodexAppServerClient> createClient)
    {
        _runtime = runtime;
        _createClient = createClient;
    }

    public ProviderKind Kind => ProviderKind.Codex;

    /// <summary>Codex thread bound to the current document; persisted by the caller.</summary>
    public string? ThreadId { get; set; }

    public RuntimeStatus Runtime { get; private set; } = new(RuntimeState.Missing, "Codex runtime ещё не проверен.", null);

    public CodexAccountSnapshot? Account { get; private set; }

    public CodexRateLimits? RateLimits { get; private set; }

    public async Task<ProviderStatus> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_activeThreadId is not null) { throw new InvalidOperationException("Нельзя обновлять Codex во время задачи."); }
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
        RuntimeStatus runtime = await _runtime.PrepareAsync(null, cancellationToken).ConfigureAwait(false);
        Runtime = runtime;
        if (runtime.State != RuntimeState.Ready || runtime.ExecutablePath is null)
        {
            Account = null;
            RateLimits = null;
            return new ProviderStatus(false, runtime.Message, "Codex не найден", null, []);
        }

        try { await EnsureClientAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is CodexProtocolException or IOException or InvalidOperationException)
        {
            if (_client is not null) { await _client.DisposeAsync().ConfigureAwait(false); _client = null; }
            if (!await _runtime.RestorePreviousAsync(cancellationToken).ConfigureAwait(false)) { throw; }
            runtime = _runtime.Inspect() with { Message = "Новый Codex не запустился; восстановлен предыдущий подписанный runtime." };
            await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        }
        Runtime = runtime;
        Account = await _client!.ReadAccountAsync(false, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CodexModel> models = await _client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        RateLimits = Account.Account is null ? null : await ReadRateLimitsSafeAsync(cancellationToken).ConfigureAwait(false);

        string account = Account.Account is { } item ? $"{item.Email ?? item.Type} · {item.PlanType ?? "ChatGPT"}" : "вход не выполнен";
        string usage = RateLimits?.Primary is { } primary ? $"{primary.UsedPercent:0}%" : "—";
        return new ProviderStatus(
            Account.Account?.Type == "chatgpt" && models.Count > 0,
            $"{runtime.Message}\nАккаунт: {account}" + (models.Count == 0 ? "\nКаталог моделей пуст: обновите Codex и состояние подключения." : string.Empty),
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
        if (Account?.Account?.Type != "chatgpt")
        {
            throw new ProviderException("CodexLoginRequired", "Требуется вход в аккаунт ChatGPT.");
        }
        string threadId;
        if (request.Stateless || ThreadId is null)
        {
            CodexThread thread = await _client!.StartThreadAsync(request.Model, _runtime.EmptyWorkingDirectory, cancellationToken, request.Stateless).ConfigureAwait(false);
            threadId = thread.Id;
            if (!request.Stateless) { ThreadId = threadId; _threadNeedsResume = false; }
        }
        else
        {
            if (_threadNeedsResume)
            {
                CodexThread thread = await _client!.ResumeThreadAsync(ThreadId, request.Model, _runtime.EmptyWorkingDirectory, cancellationToken).ConfigureAwait(false);
                ThreadId = thread.Id;
                _threadNeedsResume = false;
            }
            threadId = ThreadId;
        }
        ICodexAppServerClient client = _client!;
        CodexTurnCollector collector = new(threadId);
        void OnNotification(object? sender, CodexNotification notification) => collector.Accept(notification);
        void OnClosed(object? sender, EventArgs args) => collector.Fail(new IOException("Codex завершился до окончания ответа."));
        client.NotificationReceived += OnNotification;
        client.TransportClosed += OnClosed;
        _activeThreadId = threadId;
        bool succeeded = false;
        try
        {
            CodexTurn turn = await client.StartTurnAsync(threadId, request.Prompt, request.Model, request.Effort,
                request.OutputSchema, _runtime.EmptyWorkingDirectory, cancellationToken).ConfigureAwait(false);
            _activeTurnId = turn.Id;
            collector.SetTurn(turn.Id);
            string answer = await collector.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            succeeded = true;
            return answer;
        }
        finally
        {
            if (!succeeded)
            {
                if (_activeTurnId is { } turnId) { await InterruptSafeAsync(client, threadId, turnId).ConfigureAwait(false); }
                else { client.Terminate(); }
            }
            client.NotificationReceived -= OnNotification;
            client.TransportClosed -= OnClosed;
            _activeThreadId = null;
            _activeTurnId = null;
        }
    }

    public void ResetConversation()
    {
        ThreadId = null;
        _threadNeedsResume = false;
    }

    public void Interrupt()
    {
        string? threadId = _activeThreadId;
        string? turnId = _activeTurnId;
        if (_client is not null && threadId is not null && turnId is not null)
        {
            _ = InterruptSafeAsync(_client, threadId, turnId);
        }
    }

    public async Task PrepareRuntimeAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (_activeThreadId is not null) { throw new InvalidOperationException("Сначала остановите задачу."); }
        if (_client is not null) { await _client.DisposeAsync().ConfigureAwait(false); _client = null; }
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
        _runtime.Dispose();
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
            throw new FileNotFoundException("Codex runtime не найден. Откройте настройки и нажмите «Обновить Codex».");
        }

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _client = _createClient();
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

    private static async Task InterruptSafeAsync(ICodexAppServerClient client, string threadId, string turnId)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await client.InterruptTurnAsync(threadId, turnId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CodexProtocolException or InvalidOperationException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Cancellation must remain safe even if app-server has already exited.
        }
    }

}
