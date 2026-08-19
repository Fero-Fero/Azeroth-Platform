using System.Diagnostics;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

/// <summary>Runs the mpqtool sidecar on the manager to extract or pack MPQ archives.</summary>
public sealed class MpqToolCli : IMpqToolCli
{
    private readonly IMigrationImageService _imageService;
    private readonly MigrationOptions _migrationOptions;
    private readonly DockerOptions _dockerOptions;
    private readonly ILogger<MpqToolCli> _logger;

    public MpqToolCli(
        IMigrationImageService imageService,
        IOptions<MigrationOptions> migrationOptions,
        IOptions<DockerOptions> dockerOptions,
        ILogger<MpqToolCli> logger)
    {
        _imageService = imageService;
        _migrationOptions = migrationOptions.Value;
        _dockerOptions = dockerOptions.Value;
        _logger = logger;
    }

    public async Task ExtractAllAsync(string mpqPath, string outputDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDir);
        var workDir = Path.GetDirectoryName(Path.GetFullPath(mpqPath))
            ?? throw new InvalidOperationException($"Invalid MPQ path: {mpqPath}");
        var relOut = Path.GetRelativePath(workDir, outputDir).Replace('\\', '/');
        if (relOut.StartsWith("..", StringComparison.Ordinal))
        {
            // Output is outside the MPQ directory; copy the archive into the output parent.
            workDir = Path.GetDirectoryName(Path.GetFullPath(outputDir))
                ?? throw new InvalidOperationException($"Invalid extract dir: {outputDir}");
            var staged = Path.Combine(workDir, Path.GetFileName(mpqPath));
            if (!string.Equals(Path.GetFullPath(mpqPath), Path.GetFullPath(staged), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(mpqPath, staged, overwrite: true);
            }

            relOut = Path.GetFileName(outputDir);
            await RunAsync(workDir, $"extract {Quote(Path.GetFileName(staged))} {Quote(relOut)}", cancellationToken);
            return;
        }

        await RunAsync(workDir, $"extract {Quote(Path.GetFileName(mpqPath))} {Quote(relOut)}", cancellationToken);
    }

    public async Task PackPreservePathsAsync(string sourceDir, string outputMpq, CancellationToken cancellationToken)
    {
        var sourceFull = Path.GetFullPath(sourceDir);
        var parent = Path.GetDirectoryName(sourceFull)
            ?? throw new InvalidOperationException($"Invalid pack source: {sourceDir}");
        var folder = Path.GetFileName(sourceFull);
        var mpqName = Path.GetFileName(outputMpq);
        await RunAsync(parent, $"{Quote(mpqName)} {Quote(folder)} --preserve-paths", cancellationToken);
        var produced = Path.Combine(parent, mpqName);
        if (!File.Exists(produced))
        {
            throw new InvalidOperationException($"mpqtool did not produce {mpqName}.");
        }

        if (!string.Equals(Path.GetFullPath(produced), Path.GetFullPath(outputMpq), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMpq))!);
            File.Copy(produced, outputMpq, overwrite: true);
            File.Delete(produced);
        }
    }

    private async Task RunAsync(string hostWorkDir, string toolArgs, CancellationToken cancellationToken)
    {
        await _imageService.EnsureMpqToolImageAsync(cancellationToken);
        var (mountArgs, workDir) = ResolveLocalToolMount(hostWorkDir);
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"run --rm {mountArgs} -w {workDir} {_migrationOptions.MpqToolImage} {toolArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogDebug("mpqtool {Args}", startInfo.Arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start docker process for mpqtool.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mpqtool failed (exit {process.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
        }
    }

    private (string MountArgs, string WorkDir) ResolveLocalToolMount(string hostWorkDir)
    {
        var dataMount = GetManagerDataMountPath();
        if (TryGetDataVolumeSubpath(hostWorkDir, dataMount, out var relative)
            && !string.IsNullOrWhiteSpace(_dockerOptions.DataVolumeName))
        {
            var containerWork = string.IsNullOrEmpty(relative) ? "/data" : $"/data/{relative}";
            return ($"-v {_dockerOptions.DataVolumeName}:/data", containerWork);
        }

        return ($"-v \"{hostWorkDir}\":/w", "/w");
    }

    private string GetManagerDataMountPath()
    {
        var buildsPath = _dockerOptions.BuildsPath;
        if (string.IsNullOrWhiteSpace(buildsPath))
        {
            return Path.GetTempPath();
        }

        var dataMount = Path.GetDirectoryName(Path.GetFullPath(buildsPath).TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(dataMount) ? Path.GetTempPath() : dataMount;
    }

    private static bool TryGetDataVolumeSubpath(string localSourceDir, string dataMount, out string relative)
    {
        relative = string.Empty;
        if (string.Equals(Path.GetFullPath(dataMount), Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
