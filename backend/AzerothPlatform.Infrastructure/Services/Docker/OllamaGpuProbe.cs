using System.Diagnostics;
using AzerothPlatform.Core.Modules;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Probes the stack's Docker engine for an Ollama accelerator. Never inspects the manager host's
/// filesystem: the manager may be Windows while the engine (local WSL2 VM or remote SSH context)
/// is Linux.
///
/// <para>
/// Order: NVIDIA (<c>docker info</c> mentions nvidia), ROCm (<c>/dev/kfd</c> via
/// <c>docker run --device</c>), Vulkan (<c>/dev/dri</c>), then CPU. First match wins.
/// </para>
///
/// <para>
/// Docker Desktop/WSL2: the NVIDIA toolkit surfaces in <c>docker info</c>. AMD/Intel device
/// passthrough is WSL-dependent; when those nodes are absent the probe returns CPU, which is a
/// supported backend (same 1B model, no devices).
/// </para>
/// </summary>
public static class OllamaGpuProbe
{
    private static readonly TimeSpan DeviceProbeTimeout = TimeSpan.FromSeconds(30);
    private const string ProbeImage = "alpine:3.20";

    /// <param name="dockerContextArg">
    /// Empty for the local daemon, or <c>"--context {name} "</c> (trailing space) for an external engine.
    /// </param>
    public static async Task<GpuBackend> ProbeAsync(
        string dockerContextArg,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await HasNvidiaRuntimeAsync(dockerContextArg, cancellationToken))
            {
                logger.LogInformation("Ollama GPU probe selected NVIDIA.");
                return GpuBackend.Nvidia;
            }

            if (await HostDeviceExistsAsync(dockerContextArg, "/dev/kfd", cancellationToken))
            {
                logger.LogInformation("Ollama GPU probe selected ROCm (/dev/kfd).");
                return GpuBackend.Rocm;
            }

            if (await HostDeviceExistsAsync(dockerContextArg, "/dev/dri", cancellationToken))
            {
                logger.LogInformation("Ollama GPU probe selected Vulkan (/dev/dri).");
                return GpuBackend.Vulkan;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Ollama GPU probe failed; falling back to CPU.");
        }

        logger.LogInformation("Ollama GPU probe selected CPU.");
        return GpuBackend.Cpu;
    }

    /// <summary>
    /// True when the named models volume already has the Ollama library manifest for
    /// <paramref name="model"/>. Missing volume or probe failure is treated as "not present".
    /// </summary>
    public static async Task<bool> ModelVolumeHasManifestAsync(
        string dockerContextArg,
        string volumeName,
        string model,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var relative = OllamaSidecar.LibraryManifestRelativePath(model);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DeviceProbeTimeout);
            var (exit, _, _) = await RunDockerAsync(
                $"{dockerContextArg}run --rm -v {volumeName}:/root/.ollama {ProbeImage} test -e /root/.ollama/{relative}",
                timeout.Token);
            if (exit == 0)
            {
                logger.LogInformation(
                    "Ollama model {Model} is already on volume {Volume}.",
                    model,
                    volumeName);
                return true;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Ollama models-volume probe timed out for {Volume}/{Model}.", volumeName, model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Ollama models-volume probe failed for {Volume}/{Model}.", volumeName, model);
        }

        return false;
    }

    private static async Task<bool> HasNvidiaRuntimeAsync(string dockerContextArg, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DeviceProbeTimeout);
        try
        {
            var (exit, stdout, _) = await RunDockerAsync($"{dockerContextArg}info", timeout.Token);
            return exit == 0 && stdout.Contains("nvidia", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the engine whether a host device node exists by requesting it on a throwaway container.
    /// Does not use <c>File.Exists</c> on the manager (wrong OS / wrong machine).
    /// </summary>
    private static async Task<bool> HostDeviceExistsAsync(
        string dockerContextArg,
        string device,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DeviceProbeTimeout);
        try
        {
            var (exit, _, _) = await RunDockerAsync(
                $"{dockerContextArg}run --rm --device {device} {ProbeImage} true",
                timeout.Token);
            return exit == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
