using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Docker adapter using direct CLI calls and Docker.DotNet for log streaming.
/// </summary>
public sealed class DockerService : IDockerService
{
    private readonly ILogger<DockerService> _logger;
    private readonly Lazy<DockerClient> _dockerClient;

    public DockerService(IOptions<DockerOptions> options, ILogger<DockerService> logger)
    {
        _logger = logger;
        _dockerClient = new Lazy<DockerClient>(() =>
        {
            // Talk to whatever endpoint the deployment configured (Docker__SocketPath / SocketPath). In the
            // hardened compose this points at the allowlisted docker-socket-proxy over TCP instead of a raw
            // bind-mounted /var/run/docker.sock, so the manager never holds the unrestricted socket. The
            // Docker.DotNet client speaks HTTP, so a tcp:// endpoint is normalized to http://.
            var endpoint = string.IsNullOrWhiteSpace(options.Value.SocketPath)
                ? "unix:///var/run/docker.sock"
                : options.Value.SocketPath;
            if (endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = "http://" + endpoint["tcp://".Length..];
            }

            var config = new DockerClientConfiguration(new Uri(endpoint));
            return config.CreateClient();
        });
    }

    public async Task<bool> IsDockerAvailableAsync(string? dockerContext = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var (exitCode, _, _) = await RunDockerCommandWithStderrAsync(
                $"{ContextArg(dockerContext)}info",
                cancellationToken);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DockerListContainersResult> ListContainersWithEngineStatusAsync(
        string? composeProjectName = null,
        string? dockerContext = null,
        string? nameContains = null,
        CancellationToken cancellationToken = default)
    {
        var args = $"{ContextArg(dockerContext)}ps -a --format json";

        if (!string.IsNullOrWhiteSpace(composeProjectName))
        {
            args += $" --filter \"label=com.docker.compose.project={composeProjectName}\"";
        }

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            args += $" --filter \"name={nameContains}\"";
        }

        var (exitCode, output, stderr) = await RunDockerCommandWithStderrAsync(args, cancellationToken);

        if (exitCode != 0)
        {
            _logger.LogWarning("docker ps command failed with exit code {ExitCode}: {Stderr}", exitCode, stderr);
            return new DockerListContainersResult
            {
                EngineReachable = false,
                EngineError = SummarizeDockerEngineError(stderr),
                Containers = Array.Empty<ContainerStatusDto>()
            };
        }

        return new DockerListContainersResult
        {
            EngineReachable = true,
            Containers = ParseContainerList(output)
        };
    }

    public async Task<IReadOnlyList<ContainerStatusDto>> ListContainersAsync(
        string? composeProjectName = null,
        string? dockerContext = null,
        string? nameContains = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ListContainersWithEngineStatusAsync(
            composeProjectName,
            dockerContext,
            nameContains,
            cancellationToken);
        return result.Containers;
    }

    /// <summary>
    /// Builds the <c>--context {name} </c> prefix (with trailing space) for a docker CLI invocation, or
    /// an empty string for the manager's default local engine.
    /// </summary>
    private static string ContextArg(string? dockerContext) =>
        string.IsNullOrWhiteSpace(dockerContext) ? string.Empty : $"--context {dockerContext} ";

    private async Task<(int ExitCode, string Output)> RunDockerCommandAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var (exitCode, output, _) = await RunDockerCommandWithStderrAsync(arguments, cancellationToken);
        return (exitCode, output);
    }

    private static async Task<(int ExitCode, string Output, string Stderr)> RunDockerCommandWithStderrAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var process = new Process
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

        var outputLines = new List<string>();
        var errorLines = new List<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                outputLines.Add(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                errorLines.Add(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
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
                // Best effort — the caller is already bailing out.
            }

            throw;
        }

        return (process.ExitCode, string.Join('\n', outputLines), string.Join('\n', errorLines));
    }

    private static string SummarizeDockerEngineError(string stderr)
    {
        var message = stderr.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The Docker engine is not reachable.";
        }

        const string stderrPrefix = "stderr=";
        var stderrIdx = message.LastIndexOf(stderrPrefix, StringComparison.OrdinalIgnoreCase);
        if (stderrIdx >= 0)
        {
            var nested = message[(stderrIdx + stderrPrefix.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(nested))
            {
                message = nested;
            }
        }

        if (message.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            && message.Contains("docker.sock", StringComparison.OrdinalIgnoreCase))
        {
            return "Docker is running but the SSH user cannot access the Docker socket. "
                   + "On the VPC host run: sudo usermod -aG docker <ssh-user> (then log out and back in).";
        }

        if (message.Contains("docker daemon", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot connect", StringComparison.OrdinalIgnoreCase))
        {
            return "The Docker daemon is not running or not reachable. "
                   + "On the VPC host run: sudo systemctl start docker && sudo systemctl enable docker.";
        }

        return message;
    }

    private List<ContainerStatusDto> ParseContainerList(string jsonOutput)
    {
        var containers = new List<ContainerStatusDto>();
        
        if (string.IsNullOrWhiteSpace(jsonOutput))
        {
            return containers;
        }

        var lines = jsonOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            try
            {
                var container = JsonSerializer.Deserialize<DockerPsJsonOutput>(line);
                if (container != null)
                {
                    containers.Add(new ContainerStatusDto
                    {
                        ContainerId = container.ID,
                        Name = container.Names,
                        Service = ExtractComposeService(container.Labels),
                        Status = container.State,
                        Health = ExtractHealth(container.Status),
                        StartedAt = ParseCreatedAt(container.CreatedAt)
                    });
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse container JSON: {Line}", line);
            }
        }

        return containers;
    }

    public async Task StreamContainerLogsAsync(
        string containerId,
        int tail,
        Func<string, bool, Task> onLogReceived,
        string? dockerContext = null,
        CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            _logger.LogInformation("Starting log stream for container {ContainerId}, tail={Tail}", containerId, tail);

            // Use docker CLI for log streaming since Docker.DotNet's API is problematic
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"{ContextArg(dockerContext)}logs --follow --tail {tail} {containerId}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process = new Process { StartInfo = startInfo };
            
            // Handle stdout
            process.OutputDataReceived += async (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data) && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await onLogReceived(e.Data, false); // stdout
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing stdout log line");
                    }
                }
            };

            // Handle stderr
            process.ErrorDataReceived += async (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data) && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await onLogReceived(e.Data, true); // stderr
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing stderr log line");
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for cancellation or process exit
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (process != null && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error killing docker logs process");
                }
            });

            await process.WaitForExitAsync(cancellationToken);

            _logger.LogInformation("Log stream ended for container {ContainerId}", containerId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Log stream cancelled for container {ContainerId}", containerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming logs for container {ContainerId}", containerId);
            throw;
        }
        finally
        {
            if (process != null && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore
                }
            }
            process?.Dispose();
        }
    }

    /// <summary>
    /// Pulls the compose service name out of the comma-separated <c>Labels</c> string returned by
    /// <c>docker ps --format json</c> (each entry is <c>key=value</c>). Returns empty when absent.
    /// </summary>
    private static string ExtractComposeService(string labels)
    {
        if (string.IsNullOrWhiteSpace(labels))
        {
            return string.Empty;
        }

        foreach (var entry in labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator > 0 && entry.AsSpan(0, separator).Trim().SequenceEqual("com.docker.compose.service"))
            {
                return entry[(separator + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    private static string ExtractHealth(string status)
    {
        if (status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase))
        {
            return "healthy";
        }

        if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase))
        {
            return "unhealthy";
        }

        return "unknown";
    }

    private static DateTime ParseCreatedAt(string createdAt)
    {
        // Try Unix timestamp first
        if (long.TryParse(createdAt, out var unixTimestamp))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
        }

        // Docker returns format like "2026-04-25 01:23:04 +0200 CEST"
        // Strip the timezone abbreviation (CEST, CET, etc.) at the end
        var cleanedDate = System.Text.RegularExpressions.Regex.Replace(createdAt, @"\s+[A-Z]{3,4}$", "").Trim();

        // Try parsing as formatted date string with multiple formats
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss zzz",  // "2026-04-25 01:23:04 +0200"
            "yyyy-MM-dd HH:mm:ss",       // "2026-04-25 01:23:04"
        };

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(cleanedDate, format, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var dateTime))
            {
                return dateTime.ToUniversalTime();
            }
        }

        // Fallback: try general parsing
        if (DateTime.TryParse(cleanedDate, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate))
        {
            return parsedDate.ToUniversalTime();
        }

        // Last resort: return epoch time to indicate invalid/unknown
        return DateTime.UnixEpoch;
    }

    private sealed class DockerPsJsonOutput
    {
        [JsonPropertyName("ID")]
        public string ID { get; set; } = string.Empty;

        [JsonPropertyName("Names")]
        public string Names { get; set; } = string.Empty;

        [JsonPropertyName("State")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("Status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("Labels")]
        public string Labels { get; set; } = string.Empty;

        [JsonPropertyName("CreatedAt")]
        public string CreatedAt { get; set; } = string.Empty;
    }
}
