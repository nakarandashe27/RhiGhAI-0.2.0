using System.Text.Json;

namespace RhiGhAI.Core.Codex;

/// <summary>Catalog adapter for both legacy and current app-server model/list pages.</summary>
public static class CodexModelCatalog
{
    public static async Task<IReadOnlyList<CodexModel>> ReadAsync(
        Func<string?, CancellationToken, Task<JsonElement>> readPage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readPage);
        Dictionary<string, CodexModel> models = new(StringComparer.Ordinal);
        HashSet<string> cursors = new(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement page = await readPage(cursor, cancellationToken).ConfigureAwait(false);
            try
            {
                if (page.ValueKind != JsonValueKind.Object ||
                    !page.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                {
                    throw new CodexProtocolException("Несовместимый ответ model/list: отсутствует массив data.");
                }

                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("hidden", out JsonElement hidden) && hidden.GetBoolean()) { continue; }
                    string id = Required(item, "id");
                    string slug = item.TryGetProperty("model", out _) ? Required(item, "model") : id;
                    List<CodexReasoningEffort> efforts = [];
                    foreach (JsonElement effort in item.GetProperty("supportedReasoningEfforts").EnumerateArray())
                    {
                        string effortId = Required(effort, "reasoningEffort");
                        if (!efforts.Any(value => value.Id == effortId))
                        {
                            efforts.Add(new CodexReasoningEffort(effortId, Optional(effort, "description") ?? string.Empty));
                        }
                    }
                    string defaultEffort = Required(item, "defaultReasoningEffort");
                    if (efforts.Count == 0 || !efforts.Any(value => value.Id == defaultEffort))
                    {
                        throw new CodexProtocolException("Несовместимый каталог: defaultReasoningEffort отсутствует в supportedReasoningEfforts.");
                    }
                    models[slug] = new CodexModel(slug, Required(item, "displayName"),
                        Optional(item, "description") ?? string.Empty, efforts, defaultEffort,
                        item.TryGetProperty("isDefault", out JsonElement isDefault) && isDefault.GetBoolean());
                }
                cursor = Optional(page, "nextCursor");
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                throw new CodexProtocolException("Несовместимый формат model/list.", exception);
            }
            if (cursor is not null && (!cursors.Add(cursor) || cursors.Count > 100))
            {
                throw new CodexProtocolException("Некорректная пагинация model/list: повторный cursor или слишком много страниц.");
            }
        } while (cursor is not null);
        return models.Values.ToArray();
    }

    private static string Required(JsonElement item, string name) =>
        Optional(item, name) is { Length: > 0 } text ? text : throw new CodexProtocolException($"В model/list отсутствует {name}.");
    private static string? Optional(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
}
