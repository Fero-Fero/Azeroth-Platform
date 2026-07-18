using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Default <see cref="IRemoteEngineService"/> implementation backed by the docker CLI. For external
/// stacks it uses docker contexts over SSH (key material and an ssh config alias written under
/// <c>~/.ssh</c> in a marker-delimited managed block so multiple stacks can coexist); for local stacks
/// it drives the manager's own daemon with no context. Volume/tool helpers resolve the right context
/// automatically so callers share one code path.
/// </summary>
public sealed class RemoteEngineService : IRemoteEngineService
{
    private readonly ILogger<RemoteEngineService> _logger;
    private readonly DockerOptions _dockerOptions;
    private readonly ISecretProtector _secretProtector;

    public RemoteEngineService(
        ILogger<RemoteEngineService> logger,
        IOptions<DockerOptions> dockerOptions,
        ISecretProtector secretProtector)
    {
        _logger = logger;
        _dockerOptions = dockerOptions.Value;
        _secretProtector = secretProtector;
    }

    public string GetContextName(string stackId) => $"acore-ext-{stackId}";

    public async Task<string> ContextArgAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            return string.Empty;
        }

        var contextName = await EnsureContextAsync(stack, cancellationToken);
        return $"--context {contextName} ";
    }

    public async Task<string> EnsureContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            throw new InvalidOperationException("EnsureContextAsync is only valid for external stacks.");
        }

        if (string.IsNullOrWhiteSpace(stack.ExternalHost) || string.IsNullOrWhiteSpace(stack.ExternalSshUser))
        {
            throw new InvalidOperationException("External stack is missing the remote host or SSH user.");
        }

        var contextName = GetContextName(stack.Id);
        // The stored key is encrypted at rest; decrypt just-in-time to write the on-disk identity file.
        var privateKey = _secretProtector.Unprotect(stack.ExternalSshPrivateKey);
        WriteSshConfig(contextName, stack.ExternalHost.Trim(), stack.ExternalSshPort <= 0 ? 22 : stack.ExternalSshPort,
            stack.ExternalSshUser.Trim(), privateKey);
        await EnsureDockerContextAsync(contextName, cancellationToken);
        return contextName;
    }

    public async Task RemoveContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default)
    {
        var contextName = GetContextName(stack.Id);
        await RunAsync("docker", $"context rm -f {contextName}", cancellationToken, throwOnError: false);
        RemoveSshConfigBlock(contextName);
    }

    public async Task<RemoteConnectionTestResultDto> TestConnectionAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            return new RemoteConnectionTestResultDto { Success = false, Message = "Host and SSH user are required." };
        }

        // SSRF guard: refuse to dial loopback / link-local / cloud-metadata targets. This endpoint takes a
        // caller-supplied host, so without this an admin (or a stolen token) could use the manager to reach
        // internal-only services or the 169.254.169.254 metadata endpoint. Private LAN ranges are allowed
        // because legitimate remote Docker engines commonly live on a private network.
        if (await IsDisallowedRemoteHostAsync(host.Trim(), cancellationToken))
        {
            return new RemoteConnectionTestResultDto
            {
                Success = false,
                Message = "The specified host is not an allowed remote engine target (loopback and " +
                          "link-local/metadata addresses are blocked)."
            };
        }

        // Use a throwaway context name so a pre-create test doesn't collide with a real stack context.
        var contextName = $"acore-ext-test-{Guid.NewGuid():N}";
        try
        {
            WriteSshConfig(contextName, host.Trim(), sshPort <= 0 ? 22 : sshPort, user.Trim(), privateKey ?? string.Empty);
            await EnsureDockerContextAsync(contextName, cancellationToken);

            var (exit, stdout, stderr) = await RunAsync(
                "docker",
                $"--context {contextName} version --format {{{{.Server.Version}}}}",
                cancellationToken,
                throwOnError: false);

            if (exit == 0)
            {
                var version = stdout.Trim();
                return new RemoteConnectionTestResultDto
                {
                    Success = true,
                    ServerVersion = version,
                    Message = string.IsNullOrWhiteSpace(version)
                        ? "Connected to the remote Docker engine."
                        : $"Connected to remote Docker engine {version}."
                };
            }

            return new RemoteConnectionTestResultDto
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(stderr) ? "Failed to reach the remote Docker engine." : stderr.Trim()
            };
        }
        catch (Exception ex)
        {
            return new RemoteConnectionTestResultDto { Success = false, Message = ex.Message };
        }
        finally
        {
            await RunAsync("docker", $"context rm -f {contextName}", cancellationToken, throwOnError: false);
            RemoveSshConfigBlock(contextName);
        }
    }

    /// <summary>
    /// SSRF allowlist check: returns true when <paramref name="host"/> resolves to a loopback or
    /// link-local address (the latter includes the 169.254.169.254 cloud-metadata endpoint). Hostnames are
    /// resolved so a name pointing at a blocked IP (or DNS rebinding) is also caught. Private LAN ranges
    /// are deliberately allowed — real remote Docker engines commonly live there. On resolution failure we
    /// return false and let the subsequent SSH attempt fail normally.
    /// </summary>
    private static async Task<bool> IsDisallowedRemoteHostAsync(string host, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            }
            catch
            {
                return false;
            }
        }

        foreach (var ip in addresses)
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254)
                {
                    return true; // IPv4 link-local, incl. 169.254.169.254 cloud metadata
                }
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal)
            {
                return true;
            }
        }

        return false;
    }

    public async Task ShipImageAsync(ManagedStackEntity stack, string imageTag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageTag))
        {
            return;
        }

        var contextName = await EnsureContextAsync(stack, cancellationToken);

        // Stream the image over SSH without a temp file: docker save <tag> | docker --context <ctx> load.
        var command = $"docker save {imageTag} | docker --context {contextName} load";
        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to ship image '{imageTag}' to remote engine: {stderr}");
        }

        _logger.LogInformation("Shipped image {Image} to remote engine for stack {StackId}.", imageTag, stack.Id);
    }

    public async Task SeedVolumeAsync(ManagedStackEntity stack, string volumeName, string localSourceDir, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var local = stack.DeploymentTarget != DeploymentTarget.External;
        await SeedVolumeCoreAsync(contextArg, local, volumeName, localSourceDir, cancellationToken);
    }

    public Task SeedLocalVolumeAsync(string volumeName, string localSourceDir, CancellationToken cancellationToken = default)
        => SeedVolumeCoreAsync(string.Empty, local: true, volumeName, localSourceDir, cancellationToken);

    private async Task SeedVolumeCoreAsync(string contextArg, bool local, string volumeName, string localSourceDir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(localSourceDir))
        {
            return;
        }

        // Ensure the named volume exists on the engine.
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);

        // Fast path for the local daemon: when the source lives inside the manager's own data volume, do
        // a daemon-side volume-to-volume copy (no multi-GB streaming through the CLI).
        if (local && await TryDaemonSideCopyAsync(volumeName, localSourceDir, cancellationToken))
        {
            _logger.LogInformation("Seeded local volume {Volume} (daemon-side copy).", volumeName);
            return;
        }

        // Stream a tar of the local dir into a throwaway container that extracts it into the volume
        // mount: tar -C <src> -c . | docker [--context <ctx>] run --rm -i -v vol:/dest alpine ...
        var srcQuoted = ShellQuote(localSourceDir);
        var command =
            $"tar -C {srcQuoted} -cf - . | docker {contextArg}run --rm -i " +
            $"-v {volumeName}:/dest alpine:3.20 sh -c \"cd /dest && tar -xf -\"";

        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to seed volume '{volumeName}': {stderr}");
        }

        await VerifyVolumeNotEmptyAsync(contextArg, volumeName, cancellationToken);

        _logger.LogInformation("Seeded volume {Volume}.", volumeName);
    }

    private async Task VerifyVolumeNotEmptyAsync(string contextArg, string volumeName, CancellationToken cancellationToken)
    {
        // Guard against tar streams where stdin never reaches the container (empty volume, exit 0).
        var script = "find /dest -mindepth 1 -maxdepth 1 2>/dev/null | head -1 | grep -q .";
        var (exit, _, stderr) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, script, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Volume '{volumeName}' appears empty after seeding. " +
                "The stack Docker engine may not accept streamed uploads; check connectivity and try again. " +
                stderr.Trim());
        }
    }

    public async Task FetchVolumeAsync(ManagedStackEntity stack, string volumeName, string localDestinationDir, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        await FetchVolumeCoreAsync(contextArg, volumeName, localDestinationDir, cancellationToken);
    }

    public Task FetchLocalVolumeAsync(string volumeName, string localDestinationDir, CancellationToken cancellationToken = default)
        => FetchVolumeCoreAsync(string.Empty, volumeName, localDestinationDir, cancellationToken);

    public async Task CopyFileToContainerAsync(
        ManagedStackEntity stack,
        string containerName,
        string localSourcePath,
        string containerDestinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localSourcePath))
        {
            return;
        }

        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var (psExit, psOut, _) = await RunAsync(
            "docker",
            $"{contextArg}ps --filter name=^{containerName}$ --format {{{{.Names}}}}",
            cancellationToken,
            throwOnError: false);
        if (psExit != 0 || string.IsNullOrWhiteSpace(psOut))
        {
            return;
        }

        var destination = $"{containerName}:{containerDestinationPath}";
        var command = $"docker {contextArg}cp {ShellQuote(localSourcePath)} {ShellQuote(destination)}";
        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            _logger.LogWarning(
                "Failed to copy {Source} into {Container}:{Path}: {Err}",
                localSourcePath,
                containerName,
                containerDestinationPath,
                stderr);
            return;
        }

        _logger.LogInformation(
            "Copied live armory file {Source} into {Container}:{Path}.",
            localSourcePath,
            containerName,
            containerDestinationPath);
    }

    public async Task RemoveLocalVolumeAsync(string volumeName, CancellationToken cancellationToken = default)
        => await RunAsync("docker", $"volume rm -f {volumeName}", cancellationToken, throwOnError: false);

    public async Task<bool> ExtractImageDirAsync(string image, string imagePath, string localDestinationDir, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(localDestinationDir);

        // Use an explicit, clearly-prefixed name so this throwaway container is never mistaken for a
        // stack container and can always be force-removed (even if a previous run was interrupted).
        var containerName = $"azp-cfg-extract-{Guid.NewGuid():N}";
        await RunAsync("docker", $"rm -f {containerName}", cancellationToken, throwOnError: false);

        var (createExit, _, createErr) = await RunAsync("docker", $"create --name {containerName} {image}", cancellationToken, throwOnError: false);
        if (createExit != 0)
        {
            _logger.LogWarning("docker create {Image} failed while extracting {Path}: {Err}", image, imagePath, createErr);
            return false;
        }

        try
        {
            // "docker cp <name>:<path>/. <dest>" copies the directory *contents* into dest. RunShellAsync
            // so the paths are shell-quoted (dest may contain spaces on some hosts).
            var src = $"{containerName}:{imagePath.TrimEnd('/')}/.";
            var command = $"docker cp {ShellQuote(src)} {ShellQuote(localDestinationDir)}";
            var (cpExit, _, cpErr) = await RunShellAsync(command, cancellationToken);
            if (cpExit != 0)
            {
                _logger.LogWarning("docker cp {Image}:{Path} failed: {Err}", image, imagePath, cpErr);
                return false;
            }

            return true;
        }
        finally
        {
            await RunAsync("docker", $"rm -f {containerName}", cancellationToken, throwOnError: false);
        }
    }

    private async Task FetchVolumeCoreAsync(string contextArg, string volumeName, string localDestinationDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localDestinationDir);

        // Inverse of SeedVolumeAsync: a throwaway container tars the volume to stdout, and we extract it
        // into the local destination.
        var destQuoted = ShellQuote(localDestinationDir);
        var command =
            $"docker {contextArg}run --rm -i -v {volumeName}:/src alpine:3.20 sh -c \"cd /src && tar -cf - .\" " +
            $"| tar -C {destQuoted} -xf -";

        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to fetch volume '{volumeName}': {stderr}");
        }

        _logger.LogInformation("Fetched volume {Volume}.", volumeName);
    }

    public async Task<bool> VolumeExistsAsync(ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var (exit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        return exit == 0;
    }

    public async Task RemoveVolumeAsync(ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        await RunAsync("docker", $"{contextArg}volume rm -f {volumeName}", cancellationToken, throwOnError: false);
    }

    public async Task DeleteVolumePathsAsync(ManagedStackEntity stack, string volumeName, IEnumerable<string> relativePaths, CancellationToken cancellationToken = default)
        => await DeleteVolumePathsCoreAsync(stack, volumeName, relativePaths, cancellationToken);

    public Task DeleteLocalVolumePathsAsync(string volumeName, IEnumerable<string> relativePaths, CancellationToken cancellationToken = default)
        => DeleteVolumePathsCoreAsync(stack: null, volumeName, relativePaths, cancellationToken);

    private async Task DeleteVolumePathsCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {
        // Normalise to safe, volume-relative paths: forward slashes, no leading slash, no traversal.
        var paths = (relativePaths ?? Enumerable.Empty<string>())
            .Select(p => (p ?? string.Empty).Replace('\\', '/').Trim().Trim('/'))
            .Where(p => p.Length > 0 && !p.StartsWith('/') && !p.Split('/').Contains("..") && !ContainsShellMeta(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        // rm -f each path inside a throwaway container mounting the volume at /dest. Each path is
        // single-quoted so names with spaces are one argument to the container's rm.
        var targets = string.Join(" ", paths.Select(p => "/dest/" + ShellQuote(p)));
        var command = $"docker {contextArg}run --rm -v {volumeName}:/dest alpine:3.20 sh -c \"rm -rf -- {targets}\"";
        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            _logger.LogWarning("Failed to delete {Count} path(s) from volume {Volume}: {Err}", paths.Count, volumeName, stderr);
        }
        else
        {
            _logger.LogInformation("Deleted {Count} path(s) from volume {Volume}.", paths.Count, volumeName);
        }
    }

    public Task<IReadOnlyList<VolumeFileEntry>> ListVolumeFilesAsync(
        ManagedStackEntity stack,
        string volumeName,
        CancellationToken cancellationToken = default)
        => ListVolumeFilesCoreAsync(stack, volumeName, cancellationToken);

    public Task<IReadOnlyList<VolumeFileEntry>> ListLocalVolumeFilesAsync(
        string volumeName,
        CancellationToken cancellationToken = default)
        => ListVolumeFilesCoreAsync(stack: null, volumeName, cancellationToken);

    private async Task<IReadOnlyList<VolumeFileEntry>> ListVolumeFilesCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return [];
        }

        var command =
            $"docker {contextArg}run --rm -v {volumeName}:/src:ro alpine:3.20 " +
            "find /src -type f -printf '%P\\t%s\\n' 2>/dev/null";
        var (exit, output, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            _logger.LogDebug("Failed to list files in volume {Volume}: {Err}", volumeName, stderr);
            return [];
        }

        var files = new List<VolumeFileEntry>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = raw.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var relativePath = raw[..tab].Replace('\\', '/').Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(relativePath)
                || relativePath.Split('/').Contains("..", StringComparer.Ordinal))
            {
                continue;
            }

            if (!long.TryParse(raw[(tab + 1)..].Trim(), out var sizeBytes))
            {
                sizeBytes = 0;
            }

            files.Add(new VolumeFileEntry
            {
                RelativePath = relativePath,
                SizeBytes = sizeBytes,
            });
        }

        return files;
    }

    public Task<IReadOnlyList<VolumeDirectoryEntry>> ListVolumeDirectoryAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken = default)
        => ListVolumeDirectoryCoreAsync(stack, volumeName, relativePath, cancellationToken);

    public Task<VolumeTreeSummary> GetVolumeTreeSummaryAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken = default)
        => GetVolumeTreeSummaryCoreAsync(stack, volumeName, cancellationToken);

    public Task<int> CountVolumeFilesAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        string filePattern,
        CancellationToken cancellationToken = default)
        => CountVolumeFilesCoreAsync(stack, volumeName, relativePath, filePattern, cancellationToken);

    public Task<bool> VolumeSubdirExistsAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken = default)
        => VolumeSubdirExistsCoreAsync(stack, volumeName, relativePath, cancellationToken);

    public async Task ClearVolumeContentsAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken = default)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);
        var command =
            $"docker {contextArg}run --rm -v {volumeName}:/dest alpine:3.20 " +
            "sh -c \"find /dest -mindepth 1 -maxdepth 1 -exec rm -rf {} +\"";
        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to clear volume '{volumeName}': {stderr}");
        }
    }

    private async Task<IReadOnlyList<VolumeDirectoryEntry>> ListVolumeDirectoryCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return [];
        }

        var subdir = SanitizeVolumeSubdir(relativePath);
        var target = string.IsNullOrEmpty(subdir) ? "/dest" : $"/dest/{subdir}";
        var listScript =
            $"if [ ! -d \"{target}\" ]; then exit 2; fi; " +
            $"find \"{target}\" -mindepth 1 -maxdepth 1 2>/dev/null | while IFS= read -r p; do " +
            "n=\"${p##*/}\"; " +
            "if [ -d \"$p\" ]; then printf \"%s\\t4\\t0\\n\" \"$n\"; " +
            "elif [ -f \"$p\" ]; then s=$(stat -c %s \"$p\" 2>/dev/null || echo 0); printf \"%s\\t8\\t%s\\n\" \"$n\" \"$s\"; fi; " +
            "done";
        var (exit, output, _) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, listScript, cancellationToken);
        if (exit == 2)
        {
            return [];
        }

        if (exit != 0)
        {
            return [];
        }

        var parent = NormalizeVolumeRelative(relativePath);
        var entries = new List<VolumeDirectoryEntry>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('\t');
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            var name = NormalizeVolumeEntryName(parts[0]);
            if (name is "." or "..")
            {
                continue;
            }

            var isDirectory = parts[1].Trim() == "4";
            long.TryParse(parts[2].Trim(), out var sizeBytes);
            var rel = string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
            entries.Add(new VolumeDirectoryEntry
            {
                Name = name,
                RelativePath = rel,
                IsDirectory = isDirectory,
                SizeBytes = isDirectory ? 0 : sizeBytes,
            });
        }

        foreach (var entry in entries.Where(e => e.IsDirectory))
        {
            var childTarget = string.IsNullOrEmpty(subdir) ? $"/dest/{entry.Name}" : $"/dest/{subdir}/{entry.Name}";
            var countScript = $"find \"{childTarget}\" -mindepth 1 -maxdepth 1 2>/dev/null | wc -l";
            var (countExit, countOut, _) = await RunAlpineInVolumeAsync(
                contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, countScript, cancellationToken);
            if (countExit == 0 && int.TryParse(countOut.Trim(), out var count))
            {
                entry.ItemCount = count;
            }
        }

        entries.Sort((a, b) =>
            a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return entries;
    }

    private async Task<VolumeTreeSummary> GetVolumeTreeSummaryCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken)
    {
        var summary = new VolumeTreeSummary();
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return summary;
        }

        summary.VolumeExists = true;
        var summaryScript =
            "set +e; " +
            "files=$(find /dest -type f ! -name .hashcache.json ! -name .manifest.json 2>/dev/null | wc -l | tr -d \" \"); " +
            "bytes=$(du -sb /dest 2>/dev/null | cut -f1); " +
            "[ -n \"$bytes\" ] || bytes=0; " +
            "wow=0; test -f /dest/Wow.exe -o -f /dest/WoW.exe && wow=1; " +
            "mpq=0; if test -d /dest/Data; then find /dest/Data -maxdepth 1 -name \"*.MPQ\" -print -quit 2>/dev/null | grep -q . && mpq=1; fi; " +
            "printf \"AZP_SUMMARY:%s\\t%s\\t%s\\t%s\\n\" \"$files\" \"$bytes\" \"$wow\" \"$mpq\"";
        var (exit, output, _) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, summaryScript, cancellationToken);
        if (exit != 0)
        {
            return summary;
        }

        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("AZP_SUMMARY:", StringComparison.Ordinal));
        if (line is null)
        {
            return summary;
        }

        var payload = line["AZP_SUMMARY:".Length..];
        var parts = payload.Split('\t');
        if (parts.Length >= 4)
        {
            int.TryParse(parts[0].Trim(), out var fileCount);
            long.TryParse(parts[1].Trim(), out var totalBytes);
            summary.FileCount = fileCount;
            summary.TotalBytes = totalBytes;
            summary.HasWowExe = parts[2].Trim() == "1";
            summary.HasDataMpq = parts[3].Trim() == "1";
        }

        return summary;
    }

    private async Task<int> CountVolumeFilesCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        string filePattern,
        CancellationToken cancellationToken)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return 0;
        }

        var subdir = SanitizeVolumeSubdir(relativePath);
        var target = string.IsNullOrEmpty(subdir) ? "/dest" : $"/dest/{subdir}";
        var pattern = string.IsNullOrWhiteSpace(filePattern) ? "*" : filePattern.Trim();
        if (ContainsShellMeta(pattern))
        {
            throw new ArgumentException($"Unsafe file pattern: '{filePattern}'.", nameof(filePattern));
        }

        var countScript =
            $"if [ ! -d \"{target}\" ]; then exit 2; fi; " +
            $"find \"{target}\" -type f -name \"{pattern}\" 2>/dev/null | wc -l | tr -d \" \"";
        var (exit, output, _) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, countScript, cancellationToken);
        if (exit != 0)
        {
            return 0;
        }

        return int.TryParse(output.Trim(), out var count) ? count : 0;
    }

    private async Task<bool> VolumeSubdirExistsCoreAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var subdir = SanitizeVolumeSubdir(relativePath);
        if (string.IsNullOrEmpty(subdir))
        {
            return false;
        }

        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        var (inspectExit, _, _) = await RunAsync(
            "docker",
            $"{contextArg}volume inspect {volumeName}",
            cancellationToken,
            throwOnError: false);
        if (inspectExit != 0)
        {
            return false;
        }

        var script = $"test -d \"/dest/{subdir}\"";
        var (exit, _, _) = await RunAlpineInVolumeAsync(
            contextArg, volumeName, readOnly: true, mountAt: "/dest", workDir: null, script, cancellationToken);
        return exit == 0;
    }

    private static string NormalizeVolumeRelative(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/').Trim('/');

    public async Task EnsureVolumeExistsAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken = default)
    {
        var contextArg = stack is null ? string.Empty : await ContextArgAsync(stack, cancellationToken);
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);
    }

    public async Task SetVolumeOwnershipAsync(ManagedStackEntity stack, string volumeName, int uid, int gid, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        // Ensure the volume exists, then chown its contents in a throwaway root container. A fresh empty
        // volume keeps this ownership when a service container later auto-populates it from the image.
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);
        var args = $"{contextArg}run --rm -v {volumeName}:/dest alpine:3.20 chown -R {uid}:{gid} /dest";
        var (exit, _, stderr) = await RunAsync("docker", args, cancellationToken, throwOnError: false);
        if (exit != 0)
        {
            _logger.LogWarning("Failed to set ownership {Uid}:{Gid} on volume {Volume}: {Err}", uid, gid, volumeName, stderr);
        }
    }

    public async Task SetVolumeWorldReadableAsync(ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        await RunAsync("docker", $"{contextArg}volume create {volumeName}", cancellationToken, throwOnError: false);
        // a+rX = readable for all, and traversable (+x) on directories only, so nginx (or any uid) can
        // read the served tree even when the source carried restrictive (e.g. 0700) permissions.
        var args = $"{contextArg}run --rm -v {volumeName}:/dest alpine:3.20 chmod -R a+rX /dest";
        var (exit, _, stderr) = await RunAsync("docker", args, cancellationToken, throwOnError: false);
        if (exit != 0)
        {
            _logger.LogWarning("Failed to make volume {Volume} world-readable: {Err}", volumeName, stderr);
        }
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunToolWithWorkVolumeAsync(
        ManagedStackEntity stack,
        string localWorkDir,
        string image,
        string toolArgs,
        CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var workVolume = $"acore-tool-{Guid.NewGuid():N}";

        try
        {
            // Seed the work volume with the staged inputs, run the tool against /work, then pull the
            // (mutated) work dir back so callers keep operating on their local filesystem as before.
            await SeedVolumeAsync(stack, workVolume, localWorkDir, cancellationToken);

            var args = $"{contextArg}run --rm -v {workVolume}:/work {image} {toolArgs}";
            var result = await RunAsync("docker", args, cancellationToken, throwOnError: false);

            await FetchVolumeAsync(stack, workVolume, localWorkDir, cancellationToken);
            return result;
        }
        finally
        {
            await RunAsync("docker", $"{contextArg}volume rm -f {workVolume}", cancellationToken, throwOnError: false);
        }
    }

    public async Task FetchVolumeSubdirAsync(
        ManagedStackEntity stack,
        string volumeName,
        string subdir,
        string localDestinationDir,
        CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        Directory.CreateDirectory(localDestinationDir);

        var destQuoted = ShellQuote(localDestinationDir);
        var safeSubdir = SanitizeVolumeSubdir(subdir);
        var command =
            $"docker {contextArg}run --rm -i -v {volumeName}:/src alpine:3.20 sh -c \"cd /src/{safeSubdir} && tar -cf - .\" " +
            $"| tar -C {destQuoted} -xf -";

        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to fetch '{subdir}' from volume '{volumeName}': {stderr}");
        }

        _logger.LogInformation("Fetched {Subdir} from volume {Volume} for stack {StackId}.", subdir, volumeName, stack.Id);
    }

    public async Task CopyVolumeSubdirAsync(
        ManagedStackEntity stack,
        string sourceVolume,
        string sourceSubdir,
        string destVolume,
        string destSubdir,
        CancellationToken cancellationToken = default)
    {
        var srcRel = NormalizeVolumeRelative(sourceSubdir);
        var dstRel = NormalizeVolumeRelative(destSubdir);
        if (string.IsNullOrEmpty(srcRel))
        {
            throw new ArgumentException("Source subdirectory is required.", nameof(sourceSubdir));
        }

        if (string.IsNullOrEmpty(dstRel))
        {
            throw new ArgumentException("Destination subdirectory is required.", nameof(destSubdir));
        }

        _ = SanitizeVolumeSubdir(srcRel);
        _ = SanitizeVolumeSubdir(dstRel);

        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var command = string.Equals(sourceVolume, destVolume, StringComparison.Ordinal)
            ? $"docker {contextArg}run --rm -v {sourceVolume}:/w alpine:3.20 " +
              $"sh -c \"mkdir -p /w/{dstRel} && cp -a /w/{srcRel}/. /w/{dstRel}/\""
            : $"docker {contextArg}run --rm -v {sourceVolume}:/src:ro -v {destVolume}:/dest alpine:3.20 " +
              $"sh -c \"mkdir -p /dest/{dstRel} && cp -a /src/{srcRel}/. /dest/{dstRel}/\"";

        var (exit, _, stderr) = await RunShellAsync(command, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Failed to copy '{srcRel}' to '{dstRel}' on the stack engine: {stderr}");
        }

        _logger.LogInformation(
            "Copied volume subdir {Source} -> {Dest} ({SourceVolume} -> {DestVolume}) for stack {StackId}.",
            srcRel, dstRel, sourceVolume, destVolume, stack.Id);
    }

    public async Task RunVolumeShellAsync(
        ManagedStackEntity stack,
        string volumeName,
        string shellScript,
        CancellationToken cancellationToken = default)
    {
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var args = new List<string>();
        AddDockerContextArgs(args, contextArg);
        args.Add("run");
        args.Add("--rm");
        args.Add("-v");
        args.Add($"{volumeName}:/w");
        args.Add("-w");
        args.Add("/w");
        args.Add("alpine:3.20");
        args.Add("sh");
        args.Add("-c");
        args.Add(shellScript);
        var (exit, _, stderr) = await RunProcessAsync("docker", args, cancellationToken, throwOnError: false);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Volume shell command failed: {stderr}");
        }
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunToolInVolumeSubdirAsync(
        ManagedStackEntity stack,
        string volumeName,
        string workSubdir,
        string image,
        string toolArgs,
        CancellationToken cancellationToken = default)
    {
        var sub = SanitizeVolumeSubdir(workSubdir);
        var contextArg = await ContextArgAsync(stack, cancellationToken);
        var args = $"{contextArg}run --rm -v {volumeName}:/w -w /w/{sub} {image} {toolArgs}";
        return await RunAsync("docker", args, cancellationToken, throwOnError: false);
    }

    /// <summary>
    /// Attempts a daemon-side copy of <paramref name="localSourceDir"/> into <paramref name="volumeName"/>
    /// by mounting both the manager's data volume and the target volume in a helper container. Only
    /// possible when a data volume is configured, it exists on the daemon, and the source path lives
    /// under the data-volume mount (the parent of BuildsPath). Returns false to fall back to tar streaming.
    /// </summary>
    private async Task<bool> TryDaemonSideCopyAsync(string volumeName, string localSourceDir, CancellationToken cancellationToken)
    {
        var dataVolume = _dockerOptions.DataVolumeName;
        if (string.IsNullOrWhiteSpace(dataVolume))
        {
            return false;
        }

        if (!TryGetDataVolumeSubpath(localSourceDir, out var relative))
        {
            return false;
        }

        // Confirm the data volume actually exists (it won't for non-containerized dev runs).
        var (inspectExit, _, _) = await RunAsync("docker", $"volume inspect {dataVolume}", cancellationToken, throwOnError: false);
        if (inspectExit != 0)
        {
            return false;
        }

        var srcPath = string.IsNullOrEmpty(relative) ? "/src" : $"/src/{relative}";
        var inner = $"mkdir -p /dest && cp -a {srcPath}/. /dest/";
        var args =
            $"run --rm -v {dataVolume}:/src:ro -v {volumeName}:/dest alpine:3.20 sh -c \"{inner}\"";

        var (exit, _, stderr) = await RunAsync("docker", args, cancellationToken, throwOnError: false);
        if (exit != 0)
        {
            _logger.LogWarning("Daemon-side copy into {Volume} failed ({Stderr}); falling back to tar stream.", volumeName, stderr);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Computes the path of <paramref name="localSourceDir"/> relative to the manager's data-volume mount
    /// (the parent directory of <see cref="DockerOptions.BuildsPath"/>, e.g. <c>/app/data</c>). Returns
    /// false when the source is not under that mount.
    /// </summary>
    private bool TryGetDataVolumeSubpath(string localSourceDir, out string relative)
    {
        relative = string.Empty;
        var buildsPath = _dockerOptions.BuildsPath;
        if (string.IsNullOrWhiteSpace(buildsPath))
        {
            return false;
        }

        var dataMount = Path.GetDirectoryName(Path.GetFullPath(buildsPath).TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(dataMount))
        {
            return false;
        }

        var fullSource = Path.GetFullPath(localSourceDir);
        var normalizedMount = dataMount.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(fullSource, normalizedMount, StringComparison.Ordinal))
        {
            relative = string.Empty;
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

    private static string ShellQuote(string value) => "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";

    /// <summary>
    /// Runs a shell script in a throwaway Alpine container via the docker CLI directly (not
    /// <see cref="RunShellAsync"/>), so variable expansion inside the script is not broken by a
    /// second wrapping <c>/bin/sh -c</c>. The script is single-quoted for docker; do not embed
    /// single quotes in <paramref name="shellScript"/> (use double quotes for paths/literals).
    /// </summary>
    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAlpineInVolumeAsync(
        string contextArg,
        string volumeName,
        bool readOnly,
        string mountAt,
        string? workDir,
        string shellScript,
        CancellationToken cancellationToken)
    {
        var args = new List<string>();
        AddDockerContextArgs(args, contextArg);
        args.Add("run");
        args.Add("--rm");
        if (!string.IsNullOrEmpty(workDir))
        {
            args.Add("-w");
            args.Add(workDir);
        }

        args.Add("-v");
        args.Add($"{volumeName}:{mountAt}{(readOnly ? ":ro" : string.Empty)}");
        args.Add("alpine:3.20");
        args.Add("sh");
        args.Add("-c");
        args.Add(shellScript);
        return RunProcessAsync("docker", args, cancellationToken, throwOnError: false);
    }

    private static void AddDockerContextArgs(List<string> args, string contextArg)
    {
        var trimmed = (contextArg ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        foreach (var part in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            args.Add(part);
        }
    }

    private static string NormalizeVolumeEntryName(string raw)
    {
        var name = (raw ?? string.Empty).Replace('\\', '/').Trim();
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    // Characters that survive single-quoting only to be re-interpreted by the *outer* host shell that
    // RunShellAsync wraps the whole command in with double quotes (so command-substitution/escapes still
    // fire). Any volume-relative path containing one of these is rejected before it reaches the shell.
    private static readonly char[] ShellMetaChars = { '$', '`', '"', '\\', '\n', '\r', ';', '|', '&', '<', '>' };

    private static bool ContainsShellMeta(string value) => value.IndexOfAny(ShellMetaChars) >= 0;

    /// <summary>
    /// Validates a volume-relative subdirectory used inside a helper container command: forward slashes
    /// only, no leading slash, no <c>..</c> traversal, and no shell metacharacters. Returns the cleaned
    /// value or throws <see cref="ArgumentException"/>.
    /// </summary>
    private static string SanitizeVolumeSubdir(string subdir)
    {
        var value = (subdir ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.StartsWith('/')
            || value.Split('/').Contains("..")
            || ContainsShellMeta(value))
        {
            throw new ArgumentException($"Unsafe volume subdirectory: '{subdir}'.");
        }

        return value;
    }

    /// <summary>
    /// Rejects an SSH config token (host/user) that could inject additional <c>ssh_config</c> directives
    /// (e.g. a smuggled <c>ProxyCommand</c>) via embedded whitespace/newlines. Returns the trimmed value.
    /// </summary>
    private static string SanitizeSshToken(string value, string field)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
        {
            throw new ArgumentException($"Invalid SSH {field}: it must be a single token with no whitespace or control characters.");
        }

        return trimmed;
    }

    // ===== ssh config / key management =====

    private void WriteSshConfig(string contextName, string host, int port, string user, string privateKey)
    {
        // Host/User land in ssh_config as directive values; embedded whitespace/newlines could smuggle
        // extra directives (e.g. ProxyCommand → RCE). Reject anything that is not a single clean token.
        host = SanitizeSshToken(host, "host");
        user = SanitizeSshToken(user, "user");

        var sshDir = GetSshDir();
        Directory.CreateDirectory(sshDir);
        TrySetUnixMode(sshDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var keyPath = Path.Combine(sshDir, $"{contextName}.key");
        var knownHostsPath = Path.Combine(sshDir, $"{contextName}.known_hosts");
        var keyContent = privateKey.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        File.WriteAllText(keyPath, keyContent);
        TrySetUnixMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var block = new StringBuilder()
            .Append(BeginMarker(contextName)).Append('\n')
            .Append($"Host {contextName}\n")
            .Append($"    HostName {host}\n")
            .Append($"    User {user}\n")
            .Append($"    Port {port}\n")
            .Append($"    IdentityFile {keyPath}\n")
            .Append("    IdentitiesOnly yes\n")
            .Append("    StrictHostKeyChecking accept-new\n")
            .Append($"    UserKnownHostsFile {knownHostsPath}\n")
            .Append(EndMarker(contextName)).Append('\n')
            .ToString();

        UpsertSshConfigBlock(contextName, block);
    }

    private void UpsertSshConfigBlock(string contextName, string block)
    {
        var configPath = Path.Combine(GetSshDir(), "config");
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var without = StripBlock(existing, contextName);
        var separator = string.IsNullOrEmpty(without) || without.EndsWith('\n') ? string.Empty : "\n";
        File.WriteAllText(configPath, without + separator + block);
        TrySetUnixMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private void RemoveSshConfigBlock(string contextName)
    {
        var sshDir = GetSshDir();
        var configPath = Path.Combine(sshDir, "config");
        if (File.Exists(configPath))
        {
            File.WriteAllText(configPath, StripBlock(File.ReadAllText(configPath), contextName));
        }

        foreach (var suffix in new[] { ".key", ".known_hosts" })
        {
            var path = Path.Combine(sshDir, $"{contextName}{suffix}");
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete {Path}", path);
            }
        }
    }

    private static string StripBlock(string content, string contextName)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var begin = BeginMarker(contextName);
        var end = EndMarker(contextName);
        var startIdx = content.IndexOf(begin, StringComparison.Ordinal);
        if (startIdx < 0)
        {
            return content;
        }

        var endIdx = content.IndexOf(end, startIdx, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            return content[..startIdx].TrimEnd('\n') + "\n";
        }

        endIdx += end.Length;
        // Swallow a trailing newline left behind by the block.
        if (endIdx < content.Length && content[endIdx] == '\n')
        {
            endIdx++;
        }

        var result = content[..startIdx] + content[endIdx..];
        return result;
    }

    private static string BeginMarker(string contextName) => $"# BEGIN {contextName} (managed by AzerothPlatform)";
    private static string EndMarker(string contextName) => $"# END {contextName}";

    private static string GetSshDir()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(home, ".ssh");
    }

    private async Task EnsureDockerContextAsync(string contextName, CancellationToken cancellationToken)
    {
        var (inspectExit, _, _) = await RunAsync("docker", $"context inspect {contextName}", cancellationToken, throwOnError: false);
        var endpoint = $"host=ssh://{contextName}";
        if (inspectExit == 0)
        {
            await RunAsync("docker", $"context update {contextName} --docker {endpoint}", cancellationToken, throwOnError: false);
        }
        else
        {
            await RunAsync("docker", $"context create {contextName} --docker {endpoint}", cancellationToken, throwOnError: true);
        }
    }

    // ===== process helpers =====

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        bool throwOnError)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
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
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (throwOnError && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments} failed ({process.ExitCode}): {stderr}");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> argumentList,
        CancellationToken cancellationToken,
        bool throwOnError)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var arg in argumentList)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (throwOnError && process.ExitCode != 0)
        {
            var rendered = argumentList.Count > 0
                ? $"{fileName} {string.Join(' ', argumentList)}"
                : fileName;
            throw new InvalidOperationException($"{rendered} failed ({process.ExitCode}): {stderr}");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(string command, CancellationToken cancellationToken)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var fileName = isWindows ? "cmd.exe" : "/bin/sh";
        var arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"";
        return await RunAsync(fileName, arguments, cancellationToken, throwOnError: false);
    }

    private void TrySetUnixMode(string path, UnixFileMode mode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set unix mode on {Path}", path);
        }
    }
}
