using System.Text.Json;
using RhiGhAI.Core.Providers;
namespace RhiGhAI.Core.Codex;

/// <summary>Correlates early/late app-server events without borrowing text from another turn.</summary>
internal sealed class CodexTurnCollector(string threadId)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<CodexNotification> _early = [];
    private string? _turnId;
    private string? _text;
    public Task<string> Completion => _completion.Task;
    public void Fail(Exception exception) => _completion.TrySetException(exception);
    public void SetTurn(string turnId)
    {
        lock (_gate)
        {
            _turnId = turnId;
            foreach (CodexNotification notification in _early) { Process(notification); }
            _early.Clear();
        }
    }
    public void Accept(CodexNotification notification)
    {
        lock (_gate)
        {
            if (Text(notification.Parameters, "threadId") != threadId) { return; }
            if (_turnId is null)
            {
                if (_early.Count >= 256) { Fail(new CodexProtocolException("Слишком много событий до turn/start.")); return; }
                _early.Add(notification);
            }
            else { Process(notification); }
        }
    }
    private void Process(CodexNotification notification)
    {
        JsonElement parameters = notification.Parameters;
        if (notification.Method is "item/started" or "item/completed" && Text(parameters, "turnId") == _turnId &&
            parameters.TryGetProperty("item", out JsonElement item))
        {
            string? type = Text(item, "type");
            if (type is not ("userMessage" or "agentMessage" or "reasoning"))
            { Fail(new ProviderException("ForbiddenToolItem", $"Codex попытался использовать запрещённый item: {type}.")); }
            else if (type == "agentMessage" && notification.Method == "item/completed") { _text = Text(item, "text") ?? _text; }
        }
        if (notification.Method != "turn/completed" || !parameters.TryGetProperty("turn", out JsonElement turn) || Text(turn, "id") != _turnId) { return; }
        if (Text(turn, "status") != "completed")
        {
            Fail(new CodexProtocolException(turn.TryGetProperty("error", out JsonElement error) ? Text(error, "message") ?? "Codex turn завершился с ошибкой." : "Codex turn остановлен."));
            return;
        }
        if (turn.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement completed in items.EnumerateArray())
            {
                if (Text(completed, "type") == "agentMessage") { _text = Text(completed, "text") ?? _text; }
            }
        }
        if (string.IsNullOrWhiteSpace(_text)) { Fail(new CodexProtocolException("Codex не вернул структурированный ответ.")); }
        else { _completion.TrySetResult(_text); }
    }
    private static string? Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
