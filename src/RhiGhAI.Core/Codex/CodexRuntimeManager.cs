using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RhiGhAI.Core.Codex;

/// <summary>
/// Locates an official Codex CLI runtime and copies it out of protected install locations
/// (notably WindowsApps) before Rhino starts it. No network access is performed here.
/// </summary>
public sealed class CodexRuntimeManager : IDisposable
{
    private readonly IReadOnlyList<string>? _candidateOverride;
    private readonly Action<string> _verify;
    private readonly Func<string, CancellationToken, Task<string>> _readVersion;
    private readonly SemaphoreSlim _preparationGate = new(1, 1);


    public CodexRuntimeManager(string? rootDirectory = null, IEnumerable<string>? candidateOverride = null)
        : this(rootDirectory, candidateOverride,
            path => PublisherVerifier.Verify(path, CodexRuntimeManifest.PublisherOrganization), ReadVersionAsync)
    { }

    internal CodexRuntimeManager(string? rootDirectory, IEnumerable<string>? candidateOverride,
        Action<string> verify, Func<string, CancellationToken, Task<string>> readVersion)
    {
        _verify = verify;
        _readVersion = readVersion;
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
            _verify(ExecutablePath);
            return new RuntimeStatus(RuntimeState.Ready, "Официальный Codex runtime готов. Авторизация общая с Codex Desktop/CLI.", ExecutablePath);
        }
        catch (Exception exception) when (exception is InvalidDataException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new RuntimeStatus(RuntimeState.Invalid, $"Codex runtime отклонён: {exception.Message}", null);
        }
    }

    public Task<RuntimeStatus> PrepareAsync(IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            await _preparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await PrepareCoreAsync(progress, cancellationToken).ConfigureAwait(false); }
            finally { _preparationGate.Release(); }
        }, cancellationToken);

    private async Task<RuntimeStatus> PrepareCoreAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        RuntimeStatus existing = Inspect();
        string? source = existing.State == RuntimeState.Ready ? ExecutablePath : null;
        string? bestVersion = source is null ? null : await _readVersion(source, cancellationToken).ConfigureAwait(false);
        foreach (string candidate in EnumerateCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(candidate) || string.Equals(Path.GetFullPath(candidate), ExecutablePath, StringComparison.OrdinalIgnoreCase)) { continue; }
                _verify(candidate);
                string version = await _readVersion(candidate, cancellationToken).ConfigureAwait(false);
                if (bestVersion is null || CompareVersions(version, bestVersion) > 0)
                {
                    source = Path.GetFullPath(candidate);
                    bestVersion = version;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or Win32Exception or IOException or UnauthorizedAccessException or TimeoutException)
            { /* Stale shims or inaccessible candidates do not invalidate the last working copy. */ }
        }
        if (source is null)
        {
            return new RuntimeStatus(RuntimeState.Missing, "Codex не найден. Установите официальный Codex Desktop/CLI или полный пакет RhiGhAI.", null);
        }
        if (source == ExecutablePath)
        {
            progress?.Report(1);
            return existing with { Message = $"Codex {bestVersion}. Авторизация общая с Codex Desktop/CLI." };
        }
        Directory.CreateDirectory(RuntimeDirectory);
        string stagingPath = Path.Combine(RuntimeDirectory, $".codex-{Guid.NewGuid():N}.tmp");
        try
        {
            await CopyAsync(source, stagingPath, progress, cancellationToken).ConfigureAwait(false);
            _verify(stagingPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(ExecutablePath))
            {
                File.Replace(stagingPath, ExecutablePath, Path.Combine(RuntimeDirectory, "codex.previous.exe"), true);
            }
            else { File.Move(stagingPath, ExecutablePath); }
            Directory.CreateDirectory(EmptyWorkingDirectory);
            return Inspect() with { Message = $"Codex {bestVersion}. Runtime обновлён из подписанной установки." };
        }
        catch (Exception exception) when (existing.State == RuntimeState.Ready && exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        {
            return existing with { Message = "Не удалось обновить Codex; сохранён предыдущий подписанный runtime." };
        }
        finally
        {
            if (File.Exists(stagingPath)) { File.Delete(stagingPath); }
        }
    }

    public Task<bool> RestorePreviousAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        await _preparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string backup = Path.Combine(RuntimeDirectory, "codex.previous.exe");
            if (!File.Exists(backup)) { return false; }
            _verify(backup);
            File.Replace(backup, ExecutablePath, Path.Combine(RuntimeDirectory, $"codex.rejected-{Guid.NewGuid():N}.exe"), true);
            return true;
        }
        finally { _preparationGate.Release(); }
    }, cancellationToken);

    internal static int CompareVersions(string left, string right)
    {
        static int[] Parts(string version)
        {
            Match match = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)(?:-alpha\.(\d+)(?:\.(\d+))?)?$", RegexOptions.CultureInvariant);
            if (!match.Success) { throw new InvalidDataException("Неподдерживаемая версия Codex runtime."); }
            return [int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : int.MaxValue,
                match.Groups[5].Success ? int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) : 0];
        }
        int[] a = Parts(left); int[] b = Parts(right);
        for (int index = 0; index < a.Length; index++)
        {
            int comparison = a[index].CompareTo(b[index]);
            if (comparison != 0) { return comparison; }
        }
        return 0;
    }

    private static async Task<string> ReadVersionAsync(string path, CancellationToken cancellationToken)
    {
        using Process process = Process.Start(new ProcessStartInfo(path, "--version")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        }) ?? throw new IOException("Не удалось проверить версию Codex.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            string text = (await output.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0 || !text.StartsWith("codex-cli ", StringComparison.Ordinal)) { throw new InvalidDataException("Runtime не вернул версию Codex."); }
            string version = text["codex-cli ".Length..];
            _ = CompareVersions(version, version);
            return version;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Проверка версии Codex превысила 5 секунд."); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); }
        }
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

    public void Dispose() => _preparationGate.Dispose();

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
