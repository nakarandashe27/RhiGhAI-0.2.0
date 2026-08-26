using System.ComponentModel;

namespace RhiGhAI.Core.Codex;

/// <summary>
/// Locates an official Codex CLI runtime and copies it out of protected install locations
/// (notably WindowsApps) before Rhino starts it. No network access is performed here.
/// </summary>
public sealed class CodexRuntimeManager
{
    private readonly IReadOnlyList<string>? _candidateOverride;

    public CodexRuntimeManager(string? rootDirectory = null, IEnumerable<string>? candidateOverride = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductInfo.Name,
            "Codex");
        _candidateOverride = candidateOverride?.ToArray();
    }

    public string RootDirectory { get; }
    // Keyed by nothing but "Current": versioning this folder made every plug-in patch re-copy a
    // ~300 MB signed runtime that had not changed.
    public string RuntimeDirectory => Path.Combine(RootDirectory, "Runtime", "Current");
    public string ExecutablePath => Path.Combine(RuntimeDirectory, "codex.exe");
    public string EmptyWorkingDirectory => Path.Combine(RootDirectory, "Workspace");

    public RuntimeStatus Inspect()
    {
        if (!File.Exists(ExecutablePath))
        {
            return new RuntimeStatus(RuntimeState.Missing, "Официальный Codex runtime ещё не подготовлен.", null);
        }

        try
        {
            PublisherVerifier.Verify(ExecutablePath, CodexRuntimeManifest.PublisherOrganization);
            return new RuntimeStatus(RuntimeState.Ready, "Официальный Codex runtime готов. Авторизация общая с Codex Desktop/CLI.", ExecutablePath);
        }
        catch (Exception exception) when (exception is InvalidDataException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new RuntimeStatus(RuntimeState.Invalid, $"Codex runtime отклонён: {exception.Message}", null);
        }
    }

    public async Task<RuntimeStatus> PrepareAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        RuntimeStatus existing = Inspect();
        if (existing.State == RuntimeState.Ready)
        {
            progress?.Report(1);
            return existing;
        }

        string? source = FindCandidate();
        if (source is null)
        {
            return new RuntimeStatus(
                RuntimeState.Missing,
                "Codex не найден. Установите официальный Codex Desktop/CLI либо используйте полный установщик RhiGhAI с включённым runtime.",
                null);
        }

        PublisherVerifier.Verify(source, CodexRuntimeManifest.PublisherOrganization);
        Directory.CreateDirectory(RuntimeDirectory);
        string stagingPath = Path.Combine(RuntimeDirectory, $".codex-{Guid.NewGuid():N}.tmp");
        try
        {
            await CopyAsync(source, stagingPath, progress, cancellationToken).ConfigureAwait(false);
            PublisherVerifier.Verify(stagingPath, CodexRuntimeManifest.PublisherOrganization);
            File.Move(stagingPath, ExecutablePath, true);
            Directory.CreateDirectory(EmptyWorkingDirectory);
            return Inspect();
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private string? FindCandidate()
    {
        foreach (string candidate in EnumerateCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(candidate) || string.Equals(candidate, ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                PublisherVerifier.Verify(candidate, CodexRuntimeManifest.PublisherOrganization);
                return Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is InvalidDataException or Win32Exception or IOException or UnauthorizedAccessException)
            {
                // Ignore stale command shims and unsigned files; only a verified OpenAI runtime is accepted.
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateCandidates()
    {
        if (_candidateOverride is not null)
        {
            foreach (string candidate in _candidateOverride)
            {
                yield return candidate;
            }

            yield break;
        }

        // Inside Rhino, AppContext.BaseDirectory is Rhino's own System folder, so the runtime bundled
        // beside the plug-in was never found through it. Probe the assembly's own folder first.
        string? assemblyDirectory = Path.GetDirectoryName(typeof(CodexRuntimeManager).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "Runtime", "codex.exe");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "Runtime", "codex.exe");

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory.Trim('"'), "codex.exe");
        }

        // Codex Desktop installs per-user into a content-addressed bin folder; newest first.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string desktopBin = Path.Combine(local, "OpenAI", "Codex", "bin");
        string[] desktopVersions;
        try
        {
            desktopVersions = Directory.Exists(desktopBin)
                ? Directory.EnumerateDirectories(desktopBin)
                    .OrderByDescending(item => Directory.GetLastWriteTimeUtc(item))
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            desktopVersions = [];
        }

        foreach (string version in desktopVersions)
        {
            yield return Path.Combine(version, "codex.exe");
        }

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(roaming, "npm", "node_modules", "@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
        yield return Path.Combine(roaming, "npm", "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");

        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        IEnumerable<string> packages;
        try
        {
            packages = Directory.EnumerateDirectories(windowsApps, "OpenAI.Codex_*_x64__2p2nqsd0c76g0")
                .OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            packages = Array.Empty<string>();
        }

        foreach (string package in packages)
        {
            yield return Path.Combine(package, "app", "resources", "codex.exe");
        }
    }

    private static async Task CopyAsync(string sourcePath, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using FileStream destination = new(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        while (true)
        {
            int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            copied += count;
            progress?.Report(source.Length == 0 ? 1 : (double)copied / source.Length);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
