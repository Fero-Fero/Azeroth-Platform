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

        _logger.LogInformation("Seeded volume {Volume}.", volumeName);
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

        var contextArg = await ContextArgAsync(stack, cancellationToken);
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
        if (value.Length == 0
            || value.StartsWith('/')
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
