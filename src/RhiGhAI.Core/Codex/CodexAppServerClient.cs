using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RhiGhAI.Core.Codex;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private Process? _process;
    private Task? _stdoutLoop;
    private long _nextId;
    private int _terminated;
    private int _disposed;

    public event EventHandler<CodexNotification>? NotificationReceived;
    public event EventHandler<string>? DiagnosticReceived;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(string executablePath, string workingDirectory, CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        Directory.CreateDirectory(workingDirectory);
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            Arguments = "app-server --listen stdio:// -c check_for_update_on_startup=false -c features.plugins=false -c mcp_servers={}",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.Environment["LOG_FORMAT"] = "json";

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить Codex app-server.");
        _stdoutLoop = ReadStdoutAsync(_process, _lifetime.Token);
        _ = ReadStderrAsync(_process, _lifetime.Token);

        await RequestAsync(
            "initialize",
            new
            {
                clientInfo = new { name = "rhigai", title = ProductInfo.Name, version = ProductInfo.Version },
                capabilities = new { experimentalApi = true }
            },
            cancellationToken).ConfigureAwait(false);
        await NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexAccountSnapshot> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken)
    {
        JsonElement result = await RequestAsync("account/read", new { refreshToken }, cancellationToken).ConfigureAwait(false);
        bool requiresAuth = result.TryGetProperty("requiresOpenaiAuth", out JsonElement requiresElement) && requiresElement.GetBoolean();
        if (!result.TryGetProperty("account", out JsonElement accountElement) || accountElement.ValueKind == JsonValueKind.Null)
        {
            return new CodexAccountSnapshot(null, requiresAuth);
        }

        CodexAccount account = new(
            RequiredString(accountElement, "type"),
            OptionalString(accountElement, "email"),
            OptionalString(accountElement, "planType"));
        return new CodexAccountSnapshot(account, requiresAuth);
    }

    public async Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await RequestAsync("model/list", new { includeHidden = false }, cancellationToken).ConfigureAwait(false);
        List<CodexModel> models = [];
        foreach (JsonElement modelElement in result.GetProperty("data").EnumerateArray())
        {
            List<CodexReasoningEffort> efforts = [];
            foreach (JsonElement effort in modelElement.GetProperty("supportedReasoningEfforts").EnumerateArray())
            {
                efforts.Add(new CodexReasoningEffort(RequiredString(effort, "reasoningEffort"), RequiredString(effort, "description")));
            }

            models.Add(new CodexModel(
                RequiredString(modelElement, "id"),
                RequiredString(modelElement, "displayName"),
                RequiredString(modelElement, "description"),
                efforts,
                RequiredString(modelElement, "defaultReasoningEffort"),
                modelElement.TryGetProperty("isDefault", out JsonElement defaultElement) && defaultElement.GetBoolean()));
        }

        return models;
    }

    public async Task<CodexRateLimits?> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await RequestAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("rateLimits", out JsonElement limits) || limits.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new CodexRateLimits(
            ParseRateLimitWindow(limits, "primary"),
            ParseRateLimitWindow(limits, "secondary"),
            OptionalString(limits, "rateLimitReachedType"));
    }

    public async Task<CodexLogin> StartChatGptLoginAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await RequestAsync(
            "account/login/start",
            new { type = "chatgpt", useHostedLoginSuccessPage = true, appBrand = "chatgpt" },
            cancellationToken).ConfigureAwait(false);
        return new CodexLogin(RequiredString(result, "loginId"), new Uri(RequiredString(result, "authUrl")));
    }

    public Task LogoutAsync(CancellationToken cancellationToken) => RequestNoResultAsync("account/logout", null, cancellationToken);

    public async Task<CodexThread> StartThreadAsync(string model, string workingDirectory, CancellationToken cancellationToken)
    {
        JsonElement result = await RequestAsync(
            "thread/start",
            new
            {
                model,
                cwd = workingDirectory,
                approvalPolicy = "never",
                sandbox = "read-only",
                environments = Array.Empty<object>(),
                selectedCapabilityRoots = Array.Empty<object>(),
                personality = "none",
                serviceName = "rhigai"
            },
            cancellationToken).ConfigureAwait(false);
        return new CodexThread(RequiredString(result.GetProperty("thread"), "id"));
    }

    public async Task<CodexThread> ResumeThreadAsync(string threadId, string model, string workingDirectory, CancellationToken cancellationToken)
    {
        JsonElement result = await RequestAsync(
            "thread/resume",
            new
            {
                threadId,
                model,
                cwd = workingDirectory,
                approvalPolicy = "never",
                sandbox = "read-only",
                environments = Array.Empty<object>(),
                selectedCapabilityRoots = Array.Empty<object>(),
                personality = "none"
            },
            cancellationToken).ConfigureAwait(false);
        return new CodexThread(RequiredString(result.GetProperty("thread"), "id"));
    }

    public async Task<CodexTurn> StartTurnAsync(
        string threadId,
        string prompt,
        string model,
        string effort,
        JsonElement outputSchema,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        JsonElement result = await RequestAsync(
            "turn/start",
            new
            {
                threadId,
                input = new[] { new { type = "text", text = prompt } },
                cwd = workingDirectory,
                environments = Array.Empty<object>(),
                approvalPolicy = "never",
                sandboxPolicy = new { type = "readOnly" },
                model,
                effort,
                summary = "concise",
                personality = "none",
                outputSchema
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement turn = result.GetProperty("turn");
        return new CodexTurn(RequiredString(turn, "id"), RequiredString(turn, "status"));
    }

    public Task InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken) =>
        RequestNoResultAsync("turn/interrupt", new { threadId, turnId }, cancellationToken);

    private async Task RequestNoResultAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        _ = await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource<JsonElement> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, source))
        {
            throw new InvalidOperationException("Duplicate JSON-RPC id.");
        }

        try
        {
            await WriteAsync(new { method, id, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await source.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { method, @params = parameters }, cancellationToken);

    private async Task WriteAsync(object envelope, CancellationToken cancellationToken)
    {
        Process process = _process ?? throw new InvalidOperationException("Codex app-server is not running.");
        string json = JsonSerializer.Serialize(envelope, _jsonOptions);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // app-server occasionally prints non-protocol text; skipping keeps the transport alive.
                    DiagnosticReceived?.Invoke(this, line);
                    continue;
                }

                using (document)
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (root.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt64(out long id) && _pending.TryGetValue(id, out TaskCompletionSource<JsonElement>? source))
                    {
                        if (root.TryGetProperty("error", out JsonElement error))
                        {
                            source.TrySetException(new CodexProtocolException(error.GetRawText()));
                        }
                        else if (root.TryGetProperty("result", out JsonElement result))
                        {
                            source.TrySetResult(result.Clone());
                        }
                        else
                        {
                            source.TrySetException(new CodexProtocolException("Codex вернул ответ без result и без error."));
                        }

                        continue;
                    }

                    if (root.TryGetProperty("method", out JsonElement methodElement))
                    {
                        string method = methodElement.GetString() ?? string.Empty;
                        JsonElement parameters = root.TryGetProperty("params", out JsonElement paramsElement) && paramsElement.ValueKind == JsonValueKind.Object
                            ? paramsElement.Clone()
                            : default;
                        try
                        {
                            NotificationReceived?.Invoke(this, new CodexNotification(method, parameters));
                        }
                        catch (Exception handlerException)
                        {
                            // A subscriber must never be able to tear down the only stdout reader.
                            DiagnosticReceived?.Invoke(this, $"notification handler failed: {handlerException}");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            FailPending(new EndOfStreamException("Codex app-server closed stdout."));
        }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                DiagnosticReceived?.Invoke(this, line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (TaskCompletionSource<JsonElement> source in _pending.Values)
        {
            source.TrySetException(exception);
        }
    }

    public void Terminate()
    {
        if (Interlocked.Exchange(ref _terminated, 1) == 0)
        {
            _lifetime.Cancel();
        }

        Process? process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process may finish between HasExited and Kill.
            }
        }

        FailPending(new OperationCanceledException("Codex app-server was stopped."));
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString() ?? throw new CodexProtocolException($"Missing {propertyName}.");

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static CodexRateLimitWindow? ParseRateLimitWindow(JsonElement limits, string propertyName)
    {
        if (!limits.TryGetProperty(propertyName, out JsonElement window) || window.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        double used = window.TryGetProperty("usedPercent", out JsonElement usedElement) ? usedElement.GetDouble() : 0;
        long? resetsAt = window.TryGetProperty("resetsAt", out JsonElement resetElement) && resetElement.ValueKind != JsonValueKind.Null
            ? resetElement.GetInt64()
            : null;
        return new CodexRateLimitWindow(used, resetsAt);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Terminate();
        if (_process is { } process)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (_stdoutLoop is not null)
        {
            try
            {
                await _stdoutLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _process?.Dispose();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }
}
