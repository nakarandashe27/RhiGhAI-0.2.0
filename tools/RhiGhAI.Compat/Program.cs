using System.Diagnostics;
using RhiGhAI.Core;
using RhiGhAI.Core.Codex;

if (args is ["--prepare-codex"])
{
    CodexRuntimeManager manager = new();
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
    RuntimeStatus status = await manager.PrepareAsync(null, timeout.Token);
    if (status.State != RuntimeState.Ready || status.ExecutablePath is null)
    {
        Console.Error.WriteLine(status.Message);
        return 4;
    }

    Console.WriteLine($"Codex runtime prepared: {status.State}");
    await using CodexAppServerClient client = new();
    client.DiagnosticReceived += (_, line) => Console.Error.WriteLine(line);
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
        client.DiagnosticReceived += (_, line) => Console.Error.WriteLine(line);
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
