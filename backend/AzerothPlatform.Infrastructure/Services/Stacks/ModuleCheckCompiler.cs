using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services.Stacks;

/// <summary>
/// Builds a deps-only image and compiles AzerothCore module CMake targets inside it.
/// </summary>
internal sealed class ModuleCheckCompiler
{
    public const string ImageName = "azeroth-platform-modcheck:latest";
    public const string ImageLabel = "azeroth-platform.modcheck=1";
    public const string DockerfileShaLabel = "azeroth-platform.modcheck.dockerfile-sha";
    public const string BuildDir = "var/azp-modcheck";
    public const string ContainerNamePrefix = "azp-modcheck-";

    private static readonly SemaphoreSlim ImageGate = new(1, 1);

    private readonly DockerOptions _dockerOptions;
    private readonly ILogger _logger;
    private readonly string? _containerName;

    public ModuleCheckCompiler(DockerOptions dockerOptions, ILogger logger, string? stackId = null)
    {
        _dockerOptions = dockerOptions;
        _logger = logger;
        _containerName = string.IsNullOrWhiteSpace(stackId) ? null : ContainerName(stackId);
    }

    public static string ContainerName(string stackId)
    {
        var cleaned = new string(stackId
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
            .ToArray());
        if (string.IsNullOrEmpty(cleaned))
        {
            cleaned = "stack";
        }

        var name = ContainerNamePrefix + cleaned;
        return name.Length <= 63 ? name : name[..63];
    }

    public static string BuildDirectory(string repoPath) =>
        Path.GetFullPath(Path.Combine(repoPath, "var", "azp-modcheck"));

    public static string ComputeFingerprint(string? coreSha, IEnumerable<ModuleCheckItemDto> items)
    {
        var parts = items
            .OrderBy(item => item.ModuleId, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.ModuleId}:{item.CommitSha ?? "-"}:{item.Branch ?? "-"}");
        var raw = $"{coreSha ?? "-"}|{string.Join('|', parts)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public async Task EnsureImageAsync(Func<string, Task> log, CancellationToken cancellationToken)
    {
        var source = ResolveSourcePath();
        var dockerfilePath = Path.Combine(source, "Dockerfile");
        var dockerfileSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dockerfilePath, cancellationToken)));

        await ImageGate.WaitAsync(cancellationToken);
        try
        {
            var existingSha = await TryGetImageLabelAsync(DockerfileShaLabel, cancellationToken);
            if (string.Equals(existingSha, dockerfileSha, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previousId = await TryInspectImageIdAsync(cancellationToken);
            await log($"Building module-check compiler image from {source}...");
            var (exit, _, err) = await RunDockerAsync(
                [
                    "build",
                    "--force-rm",
                    "-t", ImageName,
                    "--label", ImageLabel,
                    "--label", $"{DockerfileShaLabel}={dockerfileSha}",
                    "--build-arg", $"MODCHECK_DOCKERFILE_SHA={dockerfileSha}",
                    source
                ],
                workingDirectory: source,
                timeout: TimeSpan.FromMinutes(20),
                cancellationToken);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(err)
                        ? "Failed to build the module-check compiler image."
                        : err);
            }

            var newId = await TryInspectImageIdAsync(cancellationToken);
            if (!string.IsNullOrEmpty(previousId)
                && !string.Equals(previousId, newId, StringComparison.OrdinalIgnoreCase))
            {
                await RunDockerAsync(["rmi", previousId], null, TimeSpan.FromMinutes(1), cancellationToken);
            }

            await PruneDanglingModcheckImagesAsync(cancellationToken);
        }
        finally
        {
            ImageGate.Release();
        }
    }

    public (IReadOnlyList<string> VolumeArgs, string WorkDir) ResolveMount(string repoPath)
    {
        var dataMount = Path.GetDirectoryName(Path.GetFullPath(_dockerOptions.BuildsPath).TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(_dockerOptions.DataVolumeName)
            && !string.IsNullOrEmpty(dataMount)
            && TryGetDataVolumeSubpath(repoPath, dataMount, out var relative))
        {
            var work = string.IsNullOrEmpty(relative) ? "/data" : $"/data/{relative}";
            return (["-v", $"{_dockerOptions.DataVolumeName}:/data"], work);
        }

        return (["-v", $"{repoPath}:/src"], "/src");
    }

    /// <summary>
    /// The data volume is owned by another uid than the container user, so git
    /// refuses the core tree until it is listed as a safe.directory.
    /// </summary>
    private static string WithGitSafeDirectory(string script) =>
        "git config --global --add safe.directory '*' >/dev/null 2>&1 || true; " +
        "git config --global --add safe.directory \"$(pwd)\" >/dev/null 2>&1 || true; " +
        script;

    public async Task ConfigureAsync(
        IReadOnlyList<string> volumeArgs,
        string workDir,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        await log("Configuring CMake (compiles core libraries on the first run)...");
        var script =
            $"set -euo pipefail; mkdir -p {BuildDir}; cmake -S . -B {BuildDir} -G Ninja " +
            "-DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang -DCMAKE_CXX_COMPILER=clang++ " +
            "-DWITH_WARNINGS=0 -DTOOLS=0 -DSCRIPTS=static -DMODULES=static " +
            "-DWITH_COREDEBUG=0 -DUSE_SCRIPTPCH=1 -DUSE_COREPCH=1";
        var (exit, output, err) = await RunInImageAsync(volumeArgs, workDir, script, TimeSpan.FromMinutes(30), cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException(TrimError(err, output, "CMake configure failed."));
        }

        await log("CMake configure finished.");
    }

    public async Task<IReadOnlySet<string>> ListTargetsAsync(
        IReadOnlyList<string> volumeArgs,
        string workDir,
        CancellationToken cancellationToken)
    {
        // Ninja has no "help" target (that only exists for Unix Makefiles). Query the graph instead.
        var (exit, output, err) = await RunInImageAsync(
            volumeArgs,
            workDir,
            $"ninja -C {BuildDir} -t targets",
            TimeSpan.FromMinutes(2),
            cancellationToken);
        var text = exit == 0 ? output : $"{output}\n{err}";
        return ParseNinjaTargets(text);
    }

    public static HashSet<string> ParseNinjaTargets(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return names;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            var name = (colon < 0 ? line : line[..colon]).Trim();
            if (name.Length > 0
                && !name.Contains('/', StringComparison.Ordinal)
                && !name.Contains('\\', StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    public async Task<(bool Ok, string CombinedLog)> BuildTargetAsync(
        IReadOnlyList<string> volumeArgs,
        string workDir,
        string target,
        Func<string, Task>? onLine,
        CancellationToken cancellationToken)
    {
        // Invoke ninja directly. `stdbuf cmake --build` does not line-buffer the ninja child, so the UI
        // would see no progress until the compile finished.
        var script =
            "if command -v stdbuf >/dev/null 2>&1; then " +
            $"stdbuf -oL -eL ninja -C {BuildDir} {target} -j\"$(nproc)\"; " +
            "else " +
            $"ninja -C {BuildDir} {target} -j\"$(nproc)\"; " +
            "fi";
        var (exit, combined) = await RunInImageStreamingAsync(
            volumeArgs, workDir, script, TimeSpan.FromMinutes(45), onLine, cancellationToken);
        return (exit == 0, combined);
    }

    /// <summary>
    /// Maps compiler output onto catalog module ids. AzerothCore static modules compile into one
    /// <c>modules</c> library, so failures are attributed by source path under <c>modules/{id}/</c>.
    /// </summary>
    public static Dictionary<string, string> AttributeErrorsToModules(
        string log,
        IEnumerable<string> moduleIds) =>
        AttributeErrorsToModules(
            log,
            moduleIds.Select(id => new ModuleCheckItemDto { ModuleId = id, CheckoutFolder = id }));

    public static Dictionary<string, string> AttributeErrorsToModules(
        string log,
        IEnumerable<ModuleCheckItemDto> items)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(log))
        {
            return result;
        }

        var list = items.Where(item => !string.IsNullOrWhiteSpace(item.ModuleId)).ToList();
        var lines = log.Split('\n', StringSplitOptions.None);
        foreach (var item in list)
        {
            var matched = new List<string>();
            var sawError = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (LineMentionsModule(line, item.ModuleId, item.CheckoutFolder))
                {
                    matched.Add(line);
                    if (IsCompilerErrorLine(line) || NearbyLinkerError(lines, i))
                    {
                        sawError = true;
                    }
                }
                else if (IsLinkerErrorLine(line) && NearbyMentionsModule(lines, i, item))
                {
                    matched.Add(line);
                    sawError = true;
                }
            }

            if (sawError && matched.Count > 0)
            {
                result[item.ModuleId] = TrimError(string.Join('\n', matched), $"Module '{item.ModuleId}' failed to compile.");
            }
        }

        return result;
    }

    private const int LinkerContextLines = 6;

    private static bool NearbyLinkerError(string[] lines, int index)
    {
        var from = Math.Max(0, index - LinkerContextLines);
        var to = Math.Min(lines.Length - 1, index + LinkerContextLines);
        for (var i = from; i <= to; i++)
        {
            if (IsLinkerErrorLine(lines[i].TrimEnd('\r')))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NearbyMentionsModule(string[] lines, int index, ModuleCheckItemDto item)
    {
        var from = Math.Max(0, index - LinkerContextLines);
        var to = Math.Min(lines.Length - 1, index + LinkerContextLines);
        for (var i = from; i <= to; i++)
        {
            if (LineMentionsModule(lines[i].TrimEnd('\r'), item.ModuleId, item.CheckoutFolder))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Updates per-module status from a single ninja/clang line. Returns true when a row changed.
    /// </summary>
    public static bool ApplyCompileLine(string line, IReadOnlyList<ModuleCheckItemDto> items)
    {
        var moduleId = FindModuleIdInLine(line, items);
        if (moduleId is null)
        {
            return false;
        }

        var item = items.First(candidate => candidate.ModuleId.Equals(moduleId, StringComparison.OrdinalIgnoreCase));
        if (IsCompilerErrorLine(line) || line.Contains("FAILED:", StringComparison.Ordinal))
        {
            item.Status = "failed";
            item.Error = TrimError(string.IsNullOrEmpty(item.Error) ? line : item.Error + "\n" + line, line);
            return true;
        }

        if (item.Status is "pending")
        {
            item.Status = "compiling";
            return true;
        }

        return false;
    }

    public static string? FindModuleIdInLine(string line, IReadOnlyList<ModuleCheckItemDto> items)
    {
        foreach (var item in items.OrderByDescending(candidate =>
                     Math.Max(candidate.ModuleId.Length, candidate.CheckoutFolder?.Length ?? 0)))
        {
            if (LineMentionsModule(line, item.ModuleId, item.CheckoutFolder))
            {
                return item.ModuleId;
            }
        }

        return null;
    }

    public static string? FindModuleIdInLine(string line, IReadOnlyList<string> moduleIds) =>
        FindModuleIdInLine(
            line,
            moduleIds.Select(id => new ModuleCheckItemDto { ModuleId = id, CheckoutFolder = id }).ToList());

    public static bool TryParseNinjaProgress(string line, out int current, out int total)
    {
        current = 0;
        total = 0;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '[')
        {
            return false;
        }

        var slash = trimmed.IndexOf('/');
        var close = trimmed.IndexOf(']');
        if (slash < 2 || close < slash)
        {
            return false;
        }

        return int.TryParse(trimmed[1..slash], out current)
            && int.TryParse(trimmed[(slash + 1)..close], out total)
            && total > 0
            && current >= 0
            && current <= total;
    }

    public static bool LineMentionsModule(string line, string moduleId, string? checkoutFolder = null)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(moduleId))
        {
            return false;
        }

        if (LineMentionsFolder(line, moduleId))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(checkoutFolder)
            && !checkoutFolder.Equals(moduleId, StringComparison.OrdinalIgnoreCase)
            && LineMentionsFolder(line, checkoutFolder);
    }

    private static bool LineMentionsFolder(string line, string folder)
    {
        return line.IndexOf($"modules/{folder}/", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf($"modules\\{folder}\\", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf($"modules.dir/{folder}/", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf($"modules.dir\\{folder}\\", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf($"CMakeFiles/modules.dir/{folder}/", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf($"/{folder}/src/", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf($"\\{folder}\\src\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string TrimError(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var useful = lines
            .Where(line => !TryParseNinjaProgress(line, out _, out _) && !IsDockerCliNoise(line))
            .ToArray();
        if (useful.Length == 0)
        {
            return fallback;
        }

        var take = Math.Min(40, useful.Length);
        return string.Join('\n', useful.AsSpan(useful.Length - take).ToArray());
    }

    /// <summary>
    /// True when ninja linked <c>libmodules</c> and the log has no compiler/ninja failures.
    /// Docker can still return a non-zero exit after that (for example "unexpected EOF" waiting on the container).
    /// </summary>
    public static bool LooksLikeSuccessfulModulesLink(string log)
    {
        if (string.IsNullOrWhiteSpace(log) || ContainsNinjaFailure(log) || ContainsCompilerError(log))
        {
            return false;
        }

        return log.Contains("Linking CXX static library", StringComparison.OrdinalIgnoreCase)
            && (log.Contains("libmodules.a", StringComparison.OrdinalIgnoreCase)
                || log.Contains("libmodules.lib", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when ninja linked the <c>worldserver</c> executable and the log has no compiler/linker failures.
    /// Static <c>libmodules.a</c> does not resolve undefined symbols between modules; this link does.
    /// </summary>
    public static bool LooksLikeSuccessfulWorldserverLink(string log)
    {
        if (string.IsNullOrWhiteSpace(log) || ContainsNinjaFailure(log) || ContainsCompilerError(log))
        {
            return false;
        }

        return log.Contains("Linking CXX executable", StringComparison.OrdinalIgnoreCase)
            && log.Contains("worldserver", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeDockerWaitFailure(string log)
    {
        return !string.IsNullOrWhiteSpace(log)
            && (log.Contains("error waiting for container", StringComparison.OrdinalIgnoreCase)
                || log.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ContainsCompilerError(string log) =>
        !string.IsNullOrWhiteSpace(log)
        && log.Split('\n').Any(IsCompilerErrorLine);

    public static bool ContainsNinjaFailure(string log) =>
        !string.IsNullOrWhiteSpace(log)
        && (log.Contains("FAILED:", StringComparison.Ordinal)
            || log.Contains("ninja: build stopped", StringComparison.OrdinalIgnoreCase));

    private static bool IsDockerCliNoise(string line)
    {
        return line.Contains("error waiting for container", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("time=\"", StringComparison.Ordinal);
    }

    private static bool IsLinkerErrorLine(string line)
    {
        return line.Contains("undefined reference", StringComparison.OrdinalIgnoreCase)
            || line.Contains("undefined symbol", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error LNK", StringComparison.OrdinalIgnoreCase)
            || line.Contains("linker command failed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompilerErrorLine(string line)
    {
        return line.Contains(": error:", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" error:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fatal error:", StringComparison.OrdinalIgnoreCase)
            || IsLinkerErrorLine(line)
            || line.Contains("error C", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineLogs(string output, string err)
    {
        if (string.IsNullOrWhiteSpace(err))
        {
            return output ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return err;
        }

        return output + "\n" + err;
    }

    private static string TrimError(string err, string output, string fallback) =>
        TrimError(CombineLogs(output, err), fallback);

    private async Task<string?> TryInspectImageIdAsync(CancellationToken cancellationToken)
    {
        var (exit, stdout, _) = await RunDockerAsync(
            ["inspect", "-f", "{{.Id}}", ImageName],
            null,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (exit != 0)
        {
            return null;
        }

        var id = stdout.Trim();
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private async Task<string?> TryGetImageLabelAsync(string labelKey, CancellationToken cancellationToken)
    {
        var (exit, stdout, _) = await RunDockerAsync(
            ["inspect", "-f", $"{{{{index .Config.Labels \"{labelKey}\"}}}}", ImageName],
            null,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (exit != 0)
        {
            return null;
        }

        var value = stdout.Trim();
        return string.IsNullOrWhiteSpace(value) || value == "<no value>" ? null : value;
    }

    /// <summary>
    /// Removes this check's container, the CMake/ninja tree, and (optionally) the shared compiler image.
    /// Always safe to call after success, failure, or cancel.
    /// </summary>
    public async Task CleanupAfterCheckAsync(
        string repoPath,
        bool removeCompilerImage,
        Func<string, Task>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            await TryLogAsync(log, "Cleaning up module-check containers and compile artifacts...");
            await RemoveOwnContainerAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Module-check container cleanup failed.");
        }

        DeleteBuildDirectory(repoPath);

        if (!removeCompilerImage)
        {
            return;
        }

        await ImageGate.WaitAsync(cancellationToken);
        try
        {
            await TryLogAsync(log, "Removing the module-check compiler image...");
            await RunDockerAsync(["rmi", "-f", ImageName], null, TimeSpan.FromMinutes(2), cancellationToken);
            await PruneDanglingModcheckImagesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Module-check image cleanup failed.");
        }
        finally
        {
            ImageGate.Release();
        }
    }

    public static void DeleteBuildDirectory(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return;
        }

        var dir = BuildDirectory(repoPath);
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                    catch
                    {
                        // Best-effort; leftover objects are retried on the next check.
                    }
                }

                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task RemoveOwnContainerAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_containerName))
        {
            return;
        }

        await RunDockerAsync(["rm", "-f", _containerName], null, TimeSpan.FromMinutes(1), cancellationToken);
    }

    private async Task PruneDanglingModcheckImagesAsync(CancellationToken cancellationToken)
    {
        var (exit, _, err) = await RunDockerAsync(
            ["image", "prune", "-f", "--filter", $"label={ImageLabel}"],
            null,
            TimeSpan.FromMinutes(1),
            cancellationToken);
        if (exit != 0 && !string.IsNullOrWhiteSpace(err))
        {
            _logger.LogDebug("Module-check image prune: {Error}", err);
        }
    }

    private static async Task TryLogAsync(Func<string, Task>? log, string message)
    {
        if (log is not null)
        {
            await log(message);
        }
    }

    private static string ResolveSourcePath()
    {
        var candidates = new[]
        {
            "/app/module-check-src",
            Path.Combine(Environment.CurrentDirectory, "docker", "module-check"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(Path.Combine(path, "Dockerfile")))
            {
                return Path.GetFullPath(path);
            }
        }

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "docker", "module-check");
            if (File.Exists(Path.Combine(candidate, "Dockerfile")))
            {
                return Path.GetFullPath(candidate);
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            "Module-check Dockerfile not found. Expected docker/module-check next to the repo or /app/module-check-src in the manager image.");
    }

    private async Task<(int Exit, string StdOut, string StdErr)> RunInImageAsync(
        IReadOnlyList<string> volumeArgs,
        string workDir,
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await RemoveOwnContainerAsync(cancellationToken);
        var args = DockerRunArgs(volumeArgs, workDir, script, ttyEnv: false);
        return await RunDockerAsync(args, null, timeout, cancellationToken);
    }

    private async Task<(int Exit, string CombinedLog)> RunInImageStreamingAsync(
        IReadOnlyList<string> volumeArgs,
        string workDir,
        string script,
        TimeSpan timeout,
        Func<string, Task>? onLine,
        CancellationToken cancellationToken)
    {
        await RemoveOwnContainerAsync(cancellationToken);
        var args = DockerRunArgs(volumeArgs, workDir, script, ttyEnv: true);
        return await RunDockerStreamingAsync(args, null, timeout, onLine, cancellationToken);
    }

    private List<string> DockerRunArgs(
        IReadOnlyList<string> volumeArgs,
        string workDir,
        string script,
        bool ttyEnv)
    {
        var args = new List<string> { "run", "--rm", "--label", ImageLabel };
        if (ttyEnv)
        {
            args.AddRange(["-e", "TERM=dumb"]);
        }

        if (!string.IsNullOrEmpty(_containerName))
        {
            args.AddRange(["--name", _containerName]);
        }

        args.AddRange(volumeArgs);
        args.AddRange(["-w", workDir, ImageName, "/bin/bash", "-lc", WithGitSafeDirectory(script)]);
        return args;
    }

    /// <summary>
    /// Splits ninja/clang output on LF and CR so progress overwrites (`\r`) become distinct lines.
    /// Incomplete trailing text stays in <paramref name="pending"/> unless <paramref name="flush"/> is true.
    /// </summary>
    public static List<string> DrainCompileLines(StringBuilder pending, bool flush)
    {
        var lines = new List<string>();
        var text = pending.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        pending.Clear();
        if (!flush)
        {
            var lastNl = text.LastIndexOf('\n');
            if (lastNl < 0)
            {
                pending.Append(text);
                return lines;
            }

            pending.Append(text[(lastNl + 1)..]);
            text = text[..lastNl];
        }

        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private async Task<(int Exit, string CombinedLog)> RunDockerStreamingAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        Func<string, Task>? onLine,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        _logger.LogDebug("docker {Args}", string.Join(' ', arguments));
        var combined = new StringBuilder();
        var logLock = new object();
        using var callbackGate = new SemaphoreSlim(1, 1);
        process.Start();

        async Task PumpAsync(StreamReader reader)
        {
            var pending = new StringBuilder();
            var buffer = new char[2048];
            while (true)
            {
                var n = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (n == 0)
                {
                    break;
                }

                pending.Append(buffer, 0, n);
                await EmitLinesAsync(pending, flush: false);
            }

            await EmitLinesAsync(pending, flush: true);

            async Task EmitLinesAsync(StringBuilder current, bool flush)
            {
                foreach (var line in DrainCompileLines(current, flush))
                {
                    lock (logLock)
                    {
                        combined.AppendLine(line);
                    }

                    if (onLine is null)
                    {
                        continue;
                    }

                    await callbackGate.WaitAsync(cancellationToken);
                    try
                    {
                        await onLine(line);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Module-check compile line callback failed.");
                    }
                    finally
                    {
                        callbackGate.Release();
                    }
                }
            }
        }

        var stdoutTask = PumpAsync(process.StandardOutput);
        var stderrTask = PumpAsync(process.StandardError);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException($"Timed out running docker {string.Join(' ', arguments)}.");
        }
        finally
        {
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
                // Reader pumps stop when the process is killed or the caller cancels.
            }
        }

        return (process.ExitCode, combined.ToString().Trim());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private async Task<(int Exit, string StdOut, string StdErr)> RunDockerAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        _logger.LogDebug("docker {Args}", string.Join(' ', arguments));
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignored
            }

            throw new InvalidOperationException($"Timed out running docker {string.Join(' ', arguments)}.");
        }

        return (process.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim());
    }

    private static bool TryGetDataVolumeSubpath(string localSourceDir, string dataMount, out string relative)
    {
        relative = string.Empty;
        var fullSource = Path.GetFullPath(localSourceDir);
        var normalizedMount = dataMount.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(fullSource, normalizedMount, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = normalizedMount + Path.DirectorySeparatorChar;
        if (!fullSource.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        relative = fullSource[prefix.Length..].Replace('\\', '/').Trim('/');
        return true;
    }
}
