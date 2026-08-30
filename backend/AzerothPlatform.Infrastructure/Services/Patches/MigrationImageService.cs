using System.Diagnostics;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Patches;

/// <summary>
/// Builds the patch pipeline's docker sidecar images (MPQ packer and WDBX editor) once, from source
/// baked into the manager image, and caches them. Modeled on <c>ArmoryImageService</c>: the source is
/// copied into a clean working directory that serves as the build context (streamed to the daemon by
/// the docker client running inside this container), and builds are semaphore-gated per image so
/// concurrent applies never build the same image twice.
/// </summary>
public sealed class MigrationImageService : IMigrationImageService
{
    private readonly MigrationOptions _options;
    private readonly ILogger<MigrationImageService> _logger;

    private readonly SemaphoreSlim _mpqGate = new(1, 1);
    private readonly SemaphoreSlim _wdbxGate = new(1, 1);

    public MigrationImageService(IOptions<MigrationOptions> options, ILogger<MigrationImageService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task EnsureMpqToolImageAsync(CancellationToken cancellationToken = default) =>
        EnsureImageAsync(_options.MpqToolImage, _options.MpqToolSourcePath, _options.MpqToolWorkPath, _mpqGate, null, cancellationToken);

    // WDBXEditor now runs under native Mono (no Wine/x86 emulation), so build it for the host arch.
    public Task EnsureWdbxImageAsync(CancellationToken cancellationToken = default) =>
        EnsureImageAsync(_options.WdbxImage, _options.WdbxSourcePath, _options.WdbxWorkPath, _wdbxGate, null, cancellationToken);

    private async Task EnsureImageAsync(
        string imageName, string sourcePath, string workPath, SemaphoreSlim gate, string? platform, CancellationToken cancellationToken)
    {
        if (await ImageExistsAsync(imageName, cancellationToken))
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check now that we hold the lock; another caller may have just built it.
            if (await ImageExistsAsync(imageName, cancellationToken))
            {
                return;
            }

            await BuildImageAsync(imageName, sourcePath, workPath, platform, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ImageExistsAsync(string imageName, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunAsync("docker", $"images -q {imageName}", cancellationToken);
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
    }

    private async Task BuildImageAsync(string imageName, string sourcePath, string workPath, string? platform, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourcePath))
        {
            throw new InvalidOperationException(
                $"Sidecar source not found at '{sourcePath}'. Ensure the Dockerfile bakes it into the image.");
        }

        // Copy the source into a clean working directory (dropping build junk) that acts as the build
        // context.
        if (Directory.Exists(workPath))
        {
            Directory.Delete(workPath, recursive: true);
        }
        Directory.CreateDirectory(workPath);
        CopyDirectory(sourcePath, workPath);
        NormalizeShellScripts(workPath);

        // `docker build <context>` streams the context from the client's filesystem, not the daemon's.
        // This process runs inside the manager container, so the context must be a container-visible
        // path (workPath) rather than a host-translated one.
        _logger.LogInformation("Building sidecar image {Image} from {Context}...", imageName, workPath);
        // Use buildx (BuildKit): the legacy builder can't cross-build linux/amd64 on an arm64 host,
        // which we need for the x86 Wine (WDBX) image. --load places the result in the docker image store.
        var platformArg = string.IsNullOrWhiteSpace(platform) ? "" : $"--platform {platform} ";
        var (exitCode, _, stderr) = await RunAsync("docker", $"buildx build {platformArg}--load -t {imageName} \"{workPath}\"", cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Sidecar image build failed for {imageName} (exit {exitCode}): {stderr}");
        }

        _logger.LogInformation("Sidecar image {Image} built.", imageName);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            if (ShouldSkip(relative))
            {
                continue;
            }
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (ShouldSkip(relative))
            {
                continue;
            }
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool ShouldSkip(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj" or ".git" or "node_modules" or "packages");
    }

    /// <summary>
    /// Shell entrypoints copied from a Windows checkout may carry CRLF; Linux interprets the shebang
    /// as <c>bash\r</c> and fails. Strip CR before <c>docker build</c>.
    /// </summary>
    private static void NormalizeShellScripts(string root)
    {
        foreach (var script in Directory.EnumerateFiles(root, "*.sh", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(script);
            if (!text.Contains('\r'))
            {
                continue;
            }

            File.WriteAllText(script, text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal));
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
