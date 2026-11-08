using System.Text.Json;
using RhiGhAI.Core.Codex;
using RhiGhAI.Core.Providers;
using Xunit;

namespace RhiGhAI.Tests;

public sealed class CodexTests
{
    private static JsonElement Json(string json) { using JsonDocument doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static string Model(string id, string slug, string effort = "low") =>
        JsonSerializer.Serialize(new { id, model = slug, displayName = slug, defaultReasoningEffort = effort,
            supportedReasoningEfforts = new[] { new { reasoningEffort = effort, description = "fixture" } }, isDefault = true });

    [Fact]
    public void PickerPreservesAvailableSelectionAndFallsBackToReturnedDefaults()
    {
        ProviderModel sol = new("sol", "Sol", ["low", "high"], "low", true);
        ProviderModel luna = new("luna", "Luna", ["medium", "high"], "medium", false);
        Assert.Equal(1, ProviderModelSelection.ModelIndex([sol, luna], "luna"));
        Assert.Equal(0, ProviderModelSelection.ModelIndex([sol, luna], "removed"));
        Assert.Equal(-1, ProviderModelSelection.ModelIndex([], "removed"));
        Assert.Equal(1, ProviderModelSelection.EffortIndex(luna, "high"));
        Assert.Equal(0, ProviderModelSelection.EffortIndex(luna, "low"));
    }

    [Fact]
    public async Task CatalogPaginatesUsesSlugAndReplacesDuplicateCapabilities()
    {
        List<string?> requested = [];
        IReadOnlyList<CodexModel> models = await CodexModelCatalog.ReadAsync((cursor, _) =>
        {
            requested.Add(cursor);
            return Task.FromResult(cursor is null
                ? Json("{\"data\":[" + Model("picker-id", "gpt-6.1-sol") + "],\"nextCursor\":\"second\"}")
                : Json("{\"data\":[" + Model("different-id", "gpt-6.1-sol", "high") + "," + Model("luna-id", "gpt-6-luna") + "],\"nextCursor\":null}"));
        }, CancellationToken.None);
        Assert.Equal(new string?[] { null, "second" }, requested);
        Assert.Equal(2, models.Count);
        Assert.Equal("high", models[0].DefaultReasoningEffort);
        Assert.Equal("gpt-6.1-sol", models[0].Id);
    }
    [Fact]
    public async Task EmptyCatalogIsAllowedButMalformedCatalogFails()
    {
        Assert.Empty(await CodexModelCatalog.ReadAsync((_, _) => Task.FromResult(Json("{\"data\":[]}")), CancellationToken.None));
        await Assert.ThrowsAsync<CodexProtocolException>(() => CodexModelCatalog.ReadAsync((_, _) => Task.FromResult(Json("{}")), CancellationToken.None));
    }
    [Theory]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[{\"id\":\"x\"}]}")]
    [InlineData("{\"data\":false}")]
    public async Task IncompatibleShapesAreProtocolErrors(string response) =>
        await Assert.ThrowsAsync<CodexProtocolException>(() => CodexModelCatalog.ReadAsync((_, _) => Task.FromResult(Json(response)), CancellationToken.None));
    [Fact]
    public async Task RepeatedCursorCannotHang()
    {
        await Assert.ThrowsAsync<CodexProtocolException>(() => CodexModelCatalog.ReadAsync((_, _) => Task.FromResult(Json("{\"data\":[],\"nextCursor\":\"same\"}")), CancellationToken.None));
    }
    [Fact]
    public async Task HiddenModelsAreExcludedAndLegacyIdIsSupported()
    {
        string legacy = Model("legacy", "legacy").Replace("\"model\":\"legacy\",", "", StringComparison.Ordinal);
        string hidden = Model("hidden", "hidden").Replace("{", "{\"hidden\":true,", StringComparison.Ordinal);
        var models = await CodexModelCatalog.ReadAsync((_, _) => Task.FromResult(Json("{\"data\":[" + legacy + "," + hidden + "]}")), CancellationToken.None);
        Assert.Equal("legacy", Assert.Single(models).Id);
    }
    [Fact]
    public async Task UnsupportedDefaultEffortFailsClosed()
    {
        string model = Model("x", "x").Replace("\"defaultReasoningEffort\":\"low\"", "\"defaultReasoningEffort\":\"high\"", StringComparison.Ordinal);
        await Assert.ThrowsAsync<CodexProtocolException>(() => CodexModelCatalog.ReadAsync((_, _) => Task.FromResult(Json("{\"data\":[" + model + "]}")), CancellationToken.None));
    }
    [Fact]
    public async Task CollectorIgnoresLateTextAndForbiddenItemsFromOtherTurns()
    {
        CodexTurnCollector collector = new("thread");
        collector.Accept(Item("old", "agentMessage", "WRONG"));
        collector.Accept(Item("old", "commandExecution", ""));
        collector.Accept(Item("new", "agentMessage", "{\"ok\":true}"));
        collector.Accept(Completed("new"));
        collector.SetTurn("new");
        Assert.Equal("{\"ok\":true}", await collector.Completion);
    }
    [Fact]
    public async Task CollectorRejectsForbiddenItemInActiveTurn()
    {
        CodexTurnCollector collector = new("thread"); collector.SetTurn("new");
        collector.Accept(Item("new", "commandExecution", ""));
        Assert.Equal("ForbiddenToolItem", (await Assert.ThrowsAsync<ProviderException>(() => collector.Completion)).Code);
    }
    [Fact]
    public async Task CollectorFailsWhenTransportCloses()
    {
        CodexTurnCollector collector = new("thread"); collector.Fail(new IOException("closed"));
        await Assert.ThrowsAsync<IOException>(() => collector.Completion);
    }
    private static CodexNotification Item(string turn, string type, string text) => new("item/completed",
        Json(JsonSerializer.Serialize(new { threadId = "thread", turnId = turn, item = new { type, text } })));
    private static CodexNotification Completed(string turn) => new("turn/completed",
        Json(JsonSerializer.Serialize(new { threadId = "thread", turn = new { id = turn, status = "completed", items = Array.Empty<object>() } })));

    [Theory]
    [InlineData("0.159.0-alpha.12.1", "0.149.0-alpha.4.3")]
    [InlineData("0.159.0-alpha.12.1", "0.159.0-alpha.9.1")]
    [InlineData("0.159.0", "0.159.0-alpha.12.1")]
    public void RuntimeVersionsCompareNumerically(string newer, string older) => Assert.True(CodexRuntimeManager.CompareVersions(newer, older) > 0);
    [Fact]
    public async Task RuntimeUpdatesFromNewestSourceAndRetainsRollbackCopy()
    {
        using RuntimeFixture fixture = new();
        await fixture.Manager.PrepareAsync(null, CancellationToken.None);
        Assert.Equal("0.159.0-alpha.12.1", File.ReadAllText(fixture.Manager.ExecutablePath));
        Assert.True(await fixture.Manager.RestorePreviousAsync(CancellationToken.None));
        Assert.Equal("0.149.0-alpha.4.3", File.ReadAllText(fixture.Manager.ExecutablePath));
    }
    [Fact]
    public async Task InvalidStagingPreservesExistingRuntime()
    {
        using RuntimeFixture fixture = new(rejectStaging: true);
        RuntimeStatus status = await fixture.Manager.PrepareAsync(null, CancellationToken.None);
        Assert.Equal(RuntimeState.Ready, status.State);
        Assert.Equal("0.149.0-alpha.4.3", File.ReadAllText(fixture.Manager.ExecutablePath));
    }
    [Fact]
    public async Task CancellationBeforeRuntimeUpdatePreservesCurrentFile()
    {
        using RuntimeFixture fixture = new();
        using CancellationTokenSource token = new(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Manager.PrepareAsync(null, token.Token));
        Assert.Equal("0.149.0-alpha.4.3", File.ReadAllText(fixture.Manager.ExecutablePath));
    }
    [Fact]
    public async Task StatelessCodexCallsDoNotOverwriteOrResumeRhinoThread()
    {
        using RuntimeFixture fixture = new(); FakeCodex client = new();
        await using CodexPlanProvider provider = new(fixture.Manager, () => client);
        await provider.ConnectAsync(CancellationToken.None);
        PlanRequest request = new("plan", Json("{\"type\":\"object\"}"), "test", "model", "low");
        await provider.RequestJsonAsync(request, CancellationToken.None);
        string? rhinoThread = provider.ThreadId;
        await provider.RequestJsonAsync(request with { Stateless = true }, CancellationToken.None);
        await provider.RequestJsonAsync(request with { Stateless = true }, CancellationToken.None);
        await provider.RequestJsonAsync(request, CancellationToken.None);
        Assert.Equal(rhinoThread, provider.ThreadId);
        Assert.Equal<bool>([false, true, true], client.Ephemeral);
        Assert.Equal(client.TurnThreads[0], client.TurnThreads[3]);
        Assert.NotEqual(client.TurnThreads[1], client.TurnThreads[2]);
    }
    [Fact]
    public async Task CancelledCodexTurnIsInterrupted()
    {
        using RuntimeFixture fixture = new(); FakeCodex client = new() { Complete = false };
        await using CodexPlanProvider provider = new(fixture.Manager, () => client);
        await provider.ConnectAsync(CancellationToken.None);
        using CancellationTokenSource token = new();
        Task<string> request = provider.RequestJsonAsync(new PlanRequest("plan", Json("{}"), "test", "model", "low"), token.Token);
        token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(client.Interrupted);
    }
    [Fact]
    public async Task EmptyCatalogDoesNotEnableCodexSend()
    {
        using RuntimeFixture fixture = new(); FakeCodex client = new() { EmptyModels = true };
        await using CodexPlanProvider provider = new(fixture.Manager, () => client);
        Assert.False((await provider.ConnectAsync(CancellationToken.None)).Ready);
    }
    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "RhiGhAI.Tests", Guid.NewGuid().ToString("N"));
        public CodexRuntimeManager Manager { get; }
        public RuntimeFixture(bool rejectStaging = false)
        {
            Directory.CreateDirectory(_root);
            string old = Path.Combine(_root, "old.exe"); string newer = Path.Combine(_root, "new.exe");
            File.WriteAllText(old, "0.149.0-alpha.4.3"); File.WriteAllText(newer, "0.159.0-alpha.12.1");
            Manager = new CodexRuntimeManager(Path.Combine(_root, "runtime"), [old, newer],
                path => { if (rejectStaging && path.EndsWith(".tmp", StringComparison.Ordinal)) { throw new InvalidDataException(); } },
                (path, _) => Task.FromResult(File.ReadAllText(path)));
            Directory.CreateDirectory(Manager.RuntimeDirectory); File.Copy(old, Manager.ExecutablePath);
        }
        public void Dispose()
        {
            Manager.Dispose();
            // This exact directory was created by this fixture, under the test temp root.
            Directory.Delete(_root, true);
        }
    }
    private sealed class FakeCodex : ICodexAppServerClient
    {
        public event EventHandler<CodexNotification>? NotificationReceived;
        public event EventHandler? TransportClosed;
        public bool IsRunning { get; private set; }
        public bool Complete { get; init; } = true;
        public bool EmptyModels { get; init; }
        public bool Interrupted { get; private set; }
        public List<bool> Ephemeral { get; } = [];
        public List<string> TurnThreads { get; } = [];
        public Task StartAsync(string path, string cwd, CancellationToken token) { IsRunning = true; return Task.CompletedTask; }
        public Task<CodexAccountSnapshot> ReadAccountAsync(bool refresh, CancellationToken token) => Task.FromResult(new CodexAccountSnapshot(new CodexAccount("chatgpt", null, null), false));
        public Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<CodexModel>>(EmptyModels ? [] : [new CodexModel("model", "Model", "", [new CodexReasoningEffort("low", "")], "low", true)]);
        public Task<CodexRateLimits?> ReadRateLimitsAsync(CancellationToken token) => Task.FromResult<CodexRateLimits?>(null);
        public Task<CodexThread> StartThreadAsync(string model, string cwd, CancellationToken token, bool ephemeral = false)
        { Ephemeral.Add(ephemeral); return Task.FromResult(new CodexThread("thread-" + Ephemeral.Count)); }
        public Task<CodexThread> ResumeThreadAsync(string id, string model, string cwd, CancellationToken token) => Task.FromResult(new CodexThread(id));
        public Task<CodexTurn> StartTurnAsync(string thread, string prompt, string model, string effort, JsonElement schema, string cwd, CancellationToken token)
        {
            TurnThreads.Add(thread); string id = "turn-" + TurnThreads.Count;
            if (Complete)
            {
                NotificationReceived?.Invoke(this, new CodexNotification("item/completed", Json(JsonSerializer.Serialize(new { threadId = thread, turnId = id, item = new { type = "agentMessage", text = "{}" } }))));
                NotificationReceived?.Invoke(this, new CodexNotification("turn/completed", Json(JsonSerializer.Serialize(new { threadId = thread, turn = new { id, status = "completed" } }))));
            }
            return Task.FromResult(new CodexTurn(id, "inProgress"));
        }
        public Task InterruptTurnAsync(string thread, string turn, CancellationToken token) { Interrupted = true; return Task.CompletedTask; }
        public Task<CodexLogin> StartChatGptLoginAsync(CancellationToken token) => throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken token) => Task.CompletedTask;
        public void Terminate() { IsRunning = false; TransportClosed?.Invoke(this, EventArgs.Empty); }
        public ValueTask DisposeAsync() { IsRunning = false; return ValueTask.CompletedTask; }
    }
}
