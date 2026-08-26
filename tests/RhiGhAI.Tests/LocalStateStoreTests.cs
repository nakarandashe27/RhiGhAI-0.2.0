using RhiGhAI.Core.Events;
using RhiGhAI.Core.Persistence;
using RhiGhAI.Core.Providers;
using Xunit;

namespace RhiGhAI.Tests;

public sealed class LocalStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RhiGhAI.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("1", ProviderKind.OpenAiCompatible)]
    [InlineData("0", ProviderKind.Codex)]
    [InlineData("\"OpenAiCompatible\"", ProviderKind.OpenAiCompatible)]
    public void ProviderIsWrittenAsTextAndStillReadsBackTheNumbersFrom020(string stored, ProviderKind expected)
    {
        LocalStateStore store = new(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "settings.json"),
            $"{{\"schemaVersion\":1,\"retryMax\":3,\"timeoutSeconds\":180,\"defaultTarget\":\"Rhino\"," +
            $"\"modelId\":null,\"effortId\":null,\"provider\":{stored},\"endpoint\":null}}");

        RhiGhAISettings loaded = store.LoadSettings();

        Assert.Equal(expected, loaded.Provider);

        // And from now on it is written as a name, so inserting an enum member cannot silently
        // reinterpret what is already on disk.
        store.SaveSettings(loaded with { Provider = ProviderKind.OpenAiCompatible });
        Assert.Contains("\"OpenAiCompatible\"", File.ReadAllText(Path.Combine(_root, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void ConversationIdentityAndAppendOnlyTranscriptRoundTrip()
    {
        LocalStateStore store = new(_root);
        string documentPath = Path.Combine(_root, "model.3dm");
        Guid conversationId = Guid.NewGuid();
        store.SaveConversation(documentPath, "thread-1", conversationId);

        ConversationBinding? binding = store.FindConversation(documentPath);
        Assert.NotNull(binding);
        Assert.Equal("thread-1", binding.ThreadId);
        Assert.Equal(conversationId, binding.ConversationId);

        EventEnvelope first = new(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            LocalStateStore.DocumentKeyForPath(documentPath, 1),
            conversationId,
            "turn-1",
            1,
            "user",
            DateTimeOffset.UtcNow,
            "recorded",
            null,
            "Создай панель",
            null);
        EventEnvelope second = first with
        {
            EventId = Guid.NewGuid(),
            EventKind = "result",
            Message = "Готово"
        };
        store.AppendEvent(first);
        store.AppendEvent(second);

        IReadOnlyList<EventEnvelope> events = store.LoadEvents(conversationId);
        Assert.Equal(2, events.Count);
        Assert.Equal("user", events[0].EventKind);
        Assert.Equal("result", events[1].EventKind);
    }

    [Fact]
    public void SettingsAreValidatedAndPersisted()
    {
        LocalStateStore store = new(_root);
        RhiGhAISettings settings = RhiGhAISettings.Default with { RetryMax = 5, TimeoutSeconds = 240 };

        store.SaveSettings(settings);

        Assert.Equal(settings, store.LoadSettings());
    }

    [Fact]
    public void CorruptConversationStoreIsQuarantinedBeforeNewBinding()
    {
        LocalStateStore store = new(_root);
        Directory.CreateDirectory(store.RootDirectory);
        File.WriteAllText(Path.Combine(store.RootDirectory, "conversations.json"), "{broken");
        Guid conversationId = Guid.NewGuid();

        store.SaveConversation(Path.Combine(_root, "model.3dm"), "thread-new", conversationId);

        Assert.Equal(conversationId, store.FindConversation(Path.Combine(_root, "model.3dm"))?.ConversationId);
        Assert.Single(Directory.GetFiles(Path.Combine(store.RootDirectory, "Quarantine")));
    }

    public void Dispose()
    {
        string fullPath = Path.GetFullPath(_root);
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RhiGhAI.Tests"));
        if (fullPath.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, true);
        }
    }
}
