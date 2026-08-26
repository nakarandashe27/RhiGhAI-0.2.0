using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RhiGhAI.Core.Providers;

/// <summary>
/// Talks to any OpenAI-compatible /chat/completions endpoint: OpenAI, OpenRouter, DeepSeek,
/// Groq, Together, the Anthropic compatibility layer, Ollama or LM Studio on localhost.
/// </summary>
public sealed class OpenAiCompatibleProvider : IPlanProvider
{
    // ponytail: one shared client for the whole process; per-call auth travels on the request.
    // The pooled lifetime matters for multi-day Rhino sessions: without it a connection never
    // reopens and a DNS change is never picked up. The infinite timeout is intentional — every
    // call arrives with a linked token that already carries the turn deadline.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private static readonly string[] EffortLevels = ["auto", "low", "medium", "high"];

    private readonly List<Message> _history = [];
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string? _configuredModel;
    private readonly bool _anthropicStyle;
    private CancellationTokenSource? _inFlight;

    public OpenAiCompatibleProvider(string? endpoint, string? apiKey, string? configuredModel)
    {
        _endpoint = (endpoint ?? string.Empty).Trim();
        _apiKey = (apiKey ?? string.Empty).Trim();
        _configuredModel = string.IsNullOrWhiteSpace(configuredModel) ? null : configuredModel.Trim();
        _anthropicStyle = _endpoint.Contains("anthropic", StringComparison.OrdinalIgnoreCase);
    }

    public ProviderKind Kind => ProviderKind.OpenAiCompatible;

    public async Task<ProviderStatus> ConnectAsync(CancellationToken cancellationToken)
    {
        Uri modelsUri;
        try
        {
            modelsUri = Endpoint("models");
        }
        catch (ProviderException exception)
        {
            return new ProviderStatus(false, exception.Message, "неверный адрес", null, []);
        }

        if (_apiKey.Length == 0 && !modelsUri.IsLoopback)
        {
            return new ProviderStatus(false, "Укажите API-ключ провайдера.", "нет ключа", null, []);
        }

        IReadOnlyList<ProviderModel> models;
        string message;
        try
        {
            models = await ListModelsAsync(modelsUri, cancellationToken).ConfigureAwait(false);
            message = models.Count > 0
                ? $"Провайдер отвечает. Моделей в каталоге: {models.Count}."
                : "Провайдер отвечает, но каталог моделей пуст — впишите идентификатор модели вручную.";
        }
        catch (ProviderException exception) when (exception.Code == "ProviderAuth")
        {
            // A rejected key is not a missing catalogue: the provider answered, and said no. Falling
            // through would fabricate a one-model catalogue and light up [ ПОДКЛЮЧЕНО ].
            return new ProviderStatus(false, $"Провайдер отклонил ключ: {exception.Message}", "ключ отклонён", null, []);
        }
        catch (Exception exception) when (exception is HttpRequestException or ProviderException or JsonException)
        {
            // A provider without /models (or with a private catalogue) is still usable with a typed model id.
            models = [];
            message = $"Каталог моделей недоступен ({exception.Message}). Впишите идентификатор модели вручную.";
        }

        if (models.Count == 0 && _configuredModel is not null)
        {
            models = [new ProviderModel(_configuredModel, _configuredModel, EffortLevels, "auto", true)];
        }

        return new ProviderStatus(models.Count > 0, message, modelsUri.Host, null, models);
    }

    public async Task<string> RequestJsonAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ProviderException("ModelRequired", "Не выбрана модель провайдера.");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inFlight = linked;
        try
        {
            string answer;
            try
            {
                answer = await CompleteAsync(request, useSchema: true, linked.Token).ConfigureAwait(false);
            }
            catch (ProviderException exception) when (exception.Code == "SchemaUnsupported")
            {
                try
                {
                    answer = await CompleteAsync(request, useSchema: false, linked.Token).ConfigureAwait(false);
                }
                catch (ProviderException retry)
                {
                    // Without this the first message — the one that says what the provider disliked — is lost.
                    throw new ProviderException(
                        retry.Code,
                        $"{retry.Message} (первая попытка со схемой: {exception.Message})",
                        exception);
                }
            }

            if (!request.Stateless)
            {
                _history.Add(new Message("assistant", answer));
                TrimHistory();
            }

            return answer;
        }
        finally
        {
            _inFlight = null;
        }
    }

    public void ResetConversation() => _history.Clear();

    public void Interrupt()
    {
        try
        {
            _inFlight?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request completed between the read and the cancel.
        }
    }

    public ValueTask DisposeAsync()
    {
        Interrupt();
        return ValueTask.CompletedTask;
    }

    private async Task<string> CompleteAsync(PlanRequest request, bool useSchema, CancellationToken cancellationToken)
    {
        string prompt = useSchema
            ? request.Prompt
            : $"{request.Prompt}\n\nReturn one JSON object and nothing else. It must validate against this JSON Schema:\n{request.OutputSchema.GetRawText()}";
        // A Grasshopper turn carries the whole component catalogue in its prompt. Replaying it out of
        // history would send that catalogue two, three, four times over inside one conversation.
        List<Message> messages = request.Stateless
            ? [new Message("user", prompt)]
            : [.. _history, new Message("user", prompt)];

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model);
            if (!string.IsNullOrWhiteSpace(request.Effort) && !string.Equals(request.Effort, "auto", StringComparison.OrdinalIgnoreCase))
            {
                writer.WriteString("reasoning_effort", request.Effort);
            }

            writer.WriteStartArray("messages");
            foreach (Message message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("response_format");
            if (useSchema)
            {
                writer.WriteString("type", "json_schema");
                writer.WriteStartObject("json_schema");
                writer.WriteString("name", request.SchemaName);
                writer.WriteBoolean("strict", true);
                writer.WritePropertyName("schema");
                request.OutputSchema.WriteTo(writer);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteString("type", "json_object");
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, Endpoint("chat/completions"))
        {
            Content = new ByteArrayContent(buffer.ToArray())
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        Authorize(httpRequest);
        using HttpResponseMessage response = await Http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (useSchema &&
                response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented or HttpStatusCode.UnprocessableEntity &&
                MentionsSchema(body))
            {
                throw new ProviderException("SchemaUnsupported", Excerpt(body));
            }

            throw new ProviderException(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "ProviderAuth" : "ProviderHttp",
                $"Провайдер вернул {(int)response.StatusCode}: {Excerpt(body)}");
        }

        string answer = ExtractContent(body);
        if (!request.Stateless)
        {
            // Added only once the answer exists: a user message left standing without its reply makes
            // the next attempt send two user messages in a row, which some gateways reject with 400.
            _history.Add(new Message("user", prompt));
        }

        return answer;
    }

    // A 400 also covers an unknown model, a reasoning_effort the model knows nothing about and an
    // oversized context. Only a complaint that names the structured-output fields earns a second,
    // schema-less call; anything else now surfaces its own message instead of being retried blindly.
    private static bool MentionsSchema(string body) =>
        body.Contains("response_format", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("json_schema", StringComparison.OrdinalIgnoreCase);

    private void Authorize(HttpRequestMessage request)
    {
        if (_apiKey.Length == 0)
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (_anthropicStyle)
        {
            // Anthropic reads its own header instead of Authorization. Sending it to every other
            // provider only copies the key into a second header nobody asked for.
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
    }

    private async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(Uri modelsUri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, modelsUri);
        Authorize(request);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderException(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "ProviderAuth" : "ProviderHttp",
                $"{(int)response.StatusCode} {Excerpt(body)}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<ProviderModel> models = [];
        foreach (JsonElement item in data.EnumerateArray())
        {
            string? id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out JsonElement idElement)
                ? idElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            models.Add(new ProviderModel(id, id, EffortLevels, "auto", string.Equals(id, _configuredModel, StringComparison.Ordinal)));
        }

        models.Sort((left, right) => string.Compare(left.Id, right.Id, StringComparison.OrdinalIgnoreCase));
        return models;
    }

    /// <summary>
    /// Absolute URL for one API call. Built through <see cref="UriBuilder"/> rather than by gluing
    /// strings together, so a base that carries a path and a query — "https://gw.example/v1?tenant=x" —
    /// keeps both instead of folding its query into the path.
    /// </summary>
    private Uri Endpoint(string relative)
    {
        if (_endpoint.Length == 0)
        {
            throw new ProviderException("EndpointRequired", "Укажите адрес API (например https://api.openai.com/v1).");
        }

        if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https"))
        {
            throw new ProviderException("EndpointInvalid", "Адрес API должен быть абсолютным http(s) адресом.");
        }

        if (parsed.Scheme == "http" && !parsed.IsLoopback)
        {
            // The key rides on every request; plaintext is only defensible against a local runtime.
            throw new ProviderException(
                "EndpointInsecure",
                "http допустим только для localhost; для внешнего адреса укажите https.");
        }

        UriBuilder builder = new(parsed);
        string path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/chat/completions".Length];
        }

        builder.Path = $"{path}/{relative}";
        return builder.Uri;
    }

    private void TrimHistory()
    {
        // Plans are self-contained; history only carries recent intent for follow-up turns.
        const int maxMessages = 8;
        if (_history.Count > maxMessages)
        {
            _history.RemoveRange(0, _history.Count - maxMessages);
        }
    }

    internal static string ExtractContent(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            // An HTML error page from a proxy is not the model answering badly, and must not burn
            // the repair attempts that exist for the model.
            throw new ProviderException("ProviderNotJson", $"Провайдер вернул не JSON: {Excerpt(body)}", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderException("ProviderNotJson", $"Провайдер вернул не JSON-объект: {Excerpt(body)}");
            }

            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
            {
                throw new ProviderException("ProviderError", Excerpt(error.GetRawText()));
            }

            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                throw new ProviderException("EmptyResponse", "Провайдер не вернул ни одного варианта ответа.");
            }

            // Legacy /completions gateways put "text" straight on the choice, streaming shims use "delta".
            JsonElement choice = choices[0];
            JsonElement message = choice.ValueKind == JsonValueKind.Object &&
                (choice.TryGetProperty("message", out JsonElement value) || choice.TryGetProperty("delta", out value))
                    ? value
                    : choice;
            if (message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("refusal", out JsonElement refusal) &&
                refusal.ValueKind == JsonValueKind.String)
            {
                throw new ProviderException("ProviderRefusal", refusal.GetString() ?? "Модель отказалась отвечать.");
            }

            JsonElement content = default;
            if (message.ValueKind == JsonValueKind.Object)
            {
                _ = message.TryGetProperty("content", out content) || message.TryGetProperty("text", out content);
            }

            string text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Concat(content.EnumerateArray()
                    .Where(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out _))
                    .Select(part => part.GetProperty("text").GetString())),
                _ => string.Empty
            };

            text = StripFences(text).Trim();
            if (text.Length == 0)
            {
                throw new ProviderException("EmptyResponse", "Провайдер вернул пустой ответ.");
            }

            return text;
        }
    }

    internal static string StripFences(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstLineEnd = trimmed.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd < 0)
        {
            return trimmed;
        }

        string inner = trimmed[(firstLineEnd + 1)..];
        int lastFence = inner.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? inner[..lastFence] : inner;
    }

    private static string Excerpt(string body)
    {
        string collapsed = body.ReplaceLineEndings(" ").Trim();
        return collapsed.Length <= 400 ? collapsed : string.Concat(collapsed.AsSpan(0, 400), "…");
    }

    private sealed record Message(string Role, string Content);
}
