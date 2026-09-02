using System.Diagnostics;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Builds the shared <c>azeroth-platform-client</c> image from the backend source baked into the
/// manager image. The needed projects (ClientServer + ClientManifest + Core) are copied into a clean
/// working directory that serves as the build context, mirroring <see cref="ArmoryImageService"/>.
/// Can target a specific docker context so external stacks build the image on their remote engine.
/// </summary>
public sealed class ClientServerImageService : IClientServerImageService
{
    // Only these project folders are needed to build the client-server image.
    private static readonly string[] RequiredProjects =
    {
        "AzerothPlatform.Core",
        "AzerothPlatform.ClientManifest",
        "AzerothPlatform.ClientServer"
    };

    private readonly ClientServerOptions _options;
    private readonly ILogger<ClientServerImageService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientServerImageService(
        IOptions<ClientServerOptions> options,
        ILogger<ClientServerImageService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureImageAsync(string? dockerContext = null, CancellationToken cancellationToken = default)
    {
        if (await ImageExistsAsync(dockerContext, cancellationToken))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await ImageExistsAsync(dockerContext, cancellationToken))
            {
                return;
            }

            await BuildImageAsync(dockerContext, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RebuildImageAsync(string? dockerContext = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await BuildImageAsync(dockerContext, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ImageExistsAsync(string? dockerContext, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunAsync("docker", $"{ContextArg(dockerContext)}images -q {_options.ImageName}", cancellationToken);
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
    }

    private async Task BuildImageAsync(string? dockerContext, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_options.SourcePath))
        {
            throw new InvalidOperationException(
                $"Backend source not found at '{_options.SourcePath}'. Ensure the Dockerfile copies backend/ into the image.");
        }

        if (Directory.Exists(_options.WorkPath))
        {
            Directory.Delete(_options.WorkPath, recursive: true);
        }
        Directory.CreateDirectory(_options.WorkPath);

        foreach (var project in RequiredProjects)
        {
            var src = Path.Combine(_options.SourcePath, project);
            if (!Directory.Exists(src))
            {
                throw new InvalidOperationException(
                    $"Required project '{project}' not found under '{_options.SourcePath}'.");
            }
            CopyDirectory(src, Path.Combine(_options.WorkPath, project));
        }

        var dockerfile = Path.Combine(_options.WorkPath, _options.DockerfileRelativePath);
        _logger.LogInformation("Building client-server image {Image} from {Context}...", _options.ImageName, _options.WorkPath);
        var (exitCode, _, stderr) = await RunAsync(
            "docker",
            $"{ContextArg(dockerContext)}build --force-rm -t {_options.ImageName} -f \"{dockerfile}\" \"{_options.WorkPath}\"",
            cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Client-server image build failed (exit {exitCode}): {stderr}");
        }

        _logger.LogInformation("Client-server image {Image} built.", _options.ImageName);
    }

    private static string ContextArg(string? dockerContext)
        => string.IsNullOrWhiteSpace(dockerContext) ? string.Empty : $"--context {dockerContext} ";

    private static void CopyDirectory(string source, string destination)
    {
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
        return parts.Any(p => p is "bin" or "obj" or ".git" or ".vs");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
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
