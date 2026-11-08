using System.Diagnostics;
using RhiGhAI.Core;
using RhiGhAI.Core.Codex;
using RhiGhAI.Core.Providers;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;

if (args is ["--live-codex"])
{
    await using CodexPlanProvider provider = new();
    using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(4));
    ProviderStatus status = await provider.ConnectAsync(deadline.Token);
    Console.WriteLine($"runtime={provider.Runtime.Message}; authenticated={provider.Account?.Account?.Type == "chatgpt"}; models={status.Models.Count}");
    string[] requested = ["gpt-6-astra", "gpt-6.1-sol", "gpt-6-luna", "gpt-6-sol"];
    bool passed = status.Ready;
    foreach (string id in requested)
    {
        ProviderModel? model = status.Models.FirstOrDefault(item => item.Id == id);
        if (model is null) { passed = false; Console.WriteLine($"model={id}; unavailable in catalog"); continue; }
        string effort = model.Efforts.Contains("low", StringComparer.Ordinal) ? "low" : model.DefaultEffort;
        try
        {
            string answer = await provider.RequestJsonAsync(new PlanRequest("rhigai_task_plan", TaskPlanJson.OutputSchema,
                "Return only the supplied structured plan. Never use tools. Rhino units are mm. Plan one ensureLayer operation for a layer named Probe. Summary: Probe. Assumptions: empty.",
                id, effort, Stateless: true), deadline.Token);
            OperationGraph graph = TaskPlanCompiler.Compile(TaskPlanJson.Parse(answer), new ValidationContext(new HashSet<string>()));
            Console.WriteLine($"model={id}; effort={effort}; structured-output=PASS; operations={graph.Nodes.Length}; persistedThread={provider.ThreadId is not null}");
        }
        catch (Exception exception) { passed = false; Console.WriteLine($"model={id}; FAILED {exception.GetType().Name}: {exception.Message}"); }
    }
    return passed ? 0 : 5;
}

if (args is ["--live-lifecycle"])
{
    await using CodexPlanProvider provider = new();
    using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));
    await provider.ConnectAsync(deadline.Token);
    PlanRequest request = new("rhigai_task_plan", TaskPlanJson.OutputSchema,
        "Return only a valid Rhino plan. Never use tools. One ensureLayer operation for Probe. Summary Probe, assumptions empty.",
        "gpt-6.1-sol", "low");
    TaskPlanCompiler.Compile(TaskPlanJson.Parse(await provider.RequestJsonAsync(request, deadline.Token)), new ValidationContext(new HashSet<string>()));
    string? thread = provider.ThreadId;
    TaskPlanCompiler.Compile(TaskPlanJson.Parse(await provider.RequestJsonAsync(request with { Model = "gpt-6-luna", Effort = "medium", Prompt = "Continue the prior plan: rename the layer to Followup. Never use tools. Return the full structured Rhino plan." }, deadline.Token)), new ValidationContext(new HashSet<string>()));
    if (thread != provider.ThreadId) { throw new InvalidOperationException("Continuation lost its thread."); }
    Console.WriteLine($"continuation+model+effort={thread == provider.ThreadId}; threadBound={thread is not null}");
    provider.ResetConversation();
    Console.WriteLine($"newConversation={provider.ThreadId is null}");
    using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(250));
    try
    {
        await provider.RequestJsonAsync(request with { Prompt = "Never use tools. Return a full structured Rhino plan with 64 distinct ensureLayer operations for layers Probe1 to Probe64." }, stop.Token);
        Console.WriteLine("stop=completed before deadline");
    }
    catch (OperationCanceledException) { Console.WriteLine("stop=cancelled without waiting for completed notification"); }
    await provider.ConnectAsync(deadline.Token);
    Console.WriteLine("refreshAfterStop=PASS");
    return 0;
}

if (args is ["--prepare-codex"])
{
    using CodexRuntimeManager manager = new();
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
    RuntimeStatus status = await manager.PrepareAsync(null, timeout.Token);
    if (status.State != RuntimeState.Ready || status.ExecutablePath is null)
    {
        Console.Error.WriteLine(status.Message);
        return 4;
    }

    Console.WriteLine($"Codex runtime prepared: {status.State}");
    await using CodexAppServerClient client = new();
    await client.StartAsync(status.ExecutablePath, manager.EmptyWorkingDirectory, timeout.Token);
    CodexAccountSnapshot account = await client.ReadAccountAsync(false, timeout.Token);
    IReadOnlyList<CodexModel> models = await client.ListModelsAsync(timeout.Token);
    Console.WriteLine($"Codex protocol compatible: models={models.Count}, authenticated={account.Account is not null}");
    return 0;
}

if (args is ["--codex", string codexPath])
{
    string probeRoot = Path.Combine(Path.GetTempPath(), $"RhiGhAI-CodexProbe-{Guid.NewGuid():N}");
    string workingDirectory = Path.Combine(probeRoot, "workspace");
    try
    {
        await using CodexAppServerClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await client.StartAsync(codexPath, workingDirectory, timeout.Token);
        CodexAccountSnapshot account = await client.ReadAccountAsync(false, timeout.Token);
        IReadOnlyList<CodexModel> models = await client.ListModelsAsync(timeout.Token);
        Console.WriteLine($"Codex protocol compatible: models={models.Count}, authenticated={account.Account is not null}");
        return 0;
    }
    finally
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(probeRoot); attempt++)
        {
            try
            {
                Directory.Delete(probeRoot, true);
            }
            catch (Exception exception) when (attempt < 9 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Probe cleanup warning: {exception.Message}");
            }
        }
    }
}

string rhinoCommonPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    "Rhino 8",
    "System",
    "RhinoCommon.dll");

if (!File.Exists(rhinoCommonPath))
{
    Console.Error.WriteLine("RhiGhAI compatibility check failed: Rhino 8 was not found.");
    return 2;
}

FileVersionInfo version = FileVersionInfo.GetVersionInfo(rhinoCommonPath);
if (version.FileMajorPart < ProductInfo.MinimumRhinoMajor ||
    (version.FileMajorPart == ProductInfo.MinimumRhinoMajor && version.FileMinorPart < ProductInfo.MinimumRhinoMinor))
{
    Console.Error.WriteLine($"RhiGhAI requires Rhino {ProductInfo.MinimumRhinoMajor}.{ProductInfo.MinimumRhinoMinor} or newer; found {version.FileVersion}.");
    return 3;
}

Console.WriteLine($"Compatible Rhino detected: {version.ProductVersion ?? version.FileVersion}");
return 0;
