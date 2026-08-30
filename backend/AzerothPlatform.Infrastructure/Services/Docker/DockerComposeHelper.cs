using System.Diagnostics;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Helper for Docker Compose command compatibility (docker-compose vs docker compose)
/// </summary>
public static class DockerComposeHelper
{
    private static string? _detectedCommand;
    private static readonly object _lock = new();

    /// <summary>
    /// Detects which Docker Compose command is available
    /// </summary>
    /// <returns>Tuple of (command, arguments) e.g. ("docker", "compose") or ("docker-compose", "")</returns>
    public static (string Command, string ArgumentPrefix) DetectDockerCompose()
    {
        lock (_lock)
        {
            if (_detectedCommand != null)
            {
                return ParseDetectedCommand(_detectedCommand);
            }

            // Try docker compose (plugin) first - this is the modern way
            if (IsCommandAvailable("docker", "compose version"))
            {
                _detectedCommand = "plugin";
                return ("docker", "compose");
            }

            // Fall back to docker-compose (standalone)
            if (IsCommandAvailable("docker-compose", "--version"))
            {
                _detectedCommand = "standalone";
                return ("docker-compose", "");
            }

            // If neither works, default to plugin (will fail with clear error)
            _detectedCommand = "plugin";
            return ("docker", "compose");
        }
    }

    /// <summary>
    /// Gets the Docker Compose command based on configuration
    /// </summary>
    public static (string Command, string ArgumentPrefix) GetDockerCompose(string configuredMode)
    {
        return configuredMode?.ToLowerInvariant() switch
        {
            "plugin" => ("docker", "compose"),
            "standalone" => ("docker-compose", ""),
            "auto" or _ => DetectDockerCompose()
        };
    }

    private static (string Command, string ArgumentPrefix) ParseDetectedCommand(string detected)
    {
        return detected switch
        {
            "plugin" => ("docker", "compose"),
            "standalone" => ("docker-compose", ""),
            _ => ("docker", "compose")
        };
    }

    private static bool IsCommandAvailable(string command, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return false;

            process.WaitForExit(2000); // 2 second timeout
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
