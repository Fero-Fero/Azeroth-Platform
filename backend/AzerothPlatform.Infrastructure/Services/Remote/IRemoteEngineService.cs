using System.Threading.Channels;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Data.Entities;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Manages Docker "engine" access for stacks. For external (remote) stacks this is an SSH connection
/// layer (on-disk private key, ssh config Host alias, docker context over SSH); for local stacks the
/// engine is the manager's own daemon. The volume/tool helpers work against both: they resolve an
/// empty context for local and <c>--context {name}</c> for external, so callers use one code path
/// regardless of deployment target.
/// </summary>
public interface IRemoteEngineService
{
    /// <summary>
    /// Resolves the docker <c>--context</c> argument for a stack's engine: an empty string for local
    /// stacks (the manager's own daemon) or <c>"--context {name} "</c> (trailing space included) for
    /// external stacks, ensuring the context exists first.
    /// </summary>
    Task<string> ContextArgAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default);

    /// <summary>Removes a named volume on the stack's engine (best-effort).</summary>
    Task RemoveVolumeAsync(ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a one-shot tool container on the stack's engine with a single work volume mounted at
    /// <c>/work</c>: seeds the work volume from <paramref name="localWorkDir"/>, runs the image with
    /// <paramref name="toolArgs"/> passed to its entrypoint, then fetches the (possibly mutated) work
    /// volume back into <paramref name="localWorkDir"/>. The throwaway volume is removed afterwards. This
    /// is the engine-agnostic replacement for <c>docker run -v {hostWorkDir}:/work {image} {toolArgs}</c>.
    /// </summary>
    Task<(int ExitCode, string StdOut, string StdErr)> RunToolWithWorkVolumeAsync(
        ManagedStackEntity stack,
        string localWorkDir,
        string image,
        string toolArgs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches only a subdirectory of a named volume (<c>/{subdir}</c>) back into a local directory by
    /// streaming a tar. Works for both local and external engines. Used to pull a targeted slice (e.g.
    /// the live <c>dbc/</c> set) without transferring the whole volume.
    /// </summary>
    Task FetchVolumeSubdirAsync(
        ManagedStackEntity stack,
        string volumeName,
        string subdir,
        string localDestinationDir,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a subdirectory from one named volume into another on the stack's engine without staging
    /// on the manager host. Both paths are relative to each volume root.
    /// </summary>
    Task CopyVolumeSubdirAsync(
        ManagedStackEntity stack,
        string sourceVolume,
        string sourceSubdir,
        string destVolume,
        string destSubdir,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a shell script with a volume mounted at <c>/w</c> (working directory <c>/w</c>) on the stack's engine.</summary>
    Task RunVolumeShellAsync(
        ManagedStackEntity stack,
        string volumeName,
        string shellScript,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a tool container with a volume subdir as its working directory on the stack engine.</summary>
    Task<(int ExitCode, string StdOut, string StdErr)> RunToolInVolumeSubdirAsync(
        ManagedStackEntity stack,
        string volumeName,
        string workSubdir,
        string image,
        string toolArgs,
        CancellationToken cancellationToken = default);

    /// <summary>The docker context name used for a given stack (e.g. <c>acore-ext-{id}</c>).</summary>
    string GetContextName(string stackId);

    /// <summary>
    /// Ensures the SSH key, ssh config alias, and docker context exist and are current for an external
    /// stack. Returns the docker context name so callers can pass <c>--context</c> to docker commands.
    /// </summary>
    Task<string> EnsureContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the remote Docker daemon over SSH (same path as the wizard connection test). Use when
    /// docker-context CLI calls fail but SSH credentials are known-good.
    /// </summary>
    Task<(bool Available, string? Message)> ProbeRemoteDockerAsync(
        ManagedStackEntity stack,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the docker context, ssh alias, and key material for a stack (best-effort).</summary>
    Task RemoveContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken = default);

    /// <summary>Probes the remote Docker engine using the supplied connection details (pre-create test).</summary>
    Task<RemoteConnectionTestResultDto> TestConnectionAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        RemoteConnectionTestPhase phase = RemoteConnectionTestPhase.Full,
        VpcConnectionTestOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs first-time provisioning on a remote host (Docker install/start, user group, verification).
    /// Steps are executed sequentially over SSH; idempotent when Docker is already configured.
    /// </summary>
    Task<RemoteSetupResultDto> ProvisionRemoteHostAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        RemoteSetupOptionsDto options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks root and image-default users out of internet SSH. Platform access stays on the operator user.
    /// AWS keeps <c>ubuntu</c> for EC2 Instance Connect only.
    /// </summary>
        Task<RemoteSetupResultDto> FinalizeSshHardeningAsync(
            string host,
            int sshPort,
            string user,
            string privateKey,
            bool enableAwsInstanceConnect,
            CancellationToken cancellationToken = default,
            RemoteHostOs remoteOs = RemoteHostOs.Linux);

    /// <summary>
    /// Runs the VPC bootstrap shell script on a remote host over SSH (<c>bash -s</c> stdin). Prefer this
    /// over pasting into the browser terminal, which can drop newlines or exit early on errors.
    /// </summary>
    Task<RemoteBootstrapResultDto> RunVpcBootstrapScriptAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        string? scriptSshUser = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies or updates host firewall allow rules for the given player/web ports (Linux ufw).
    /// </summary>
    Task<RemoteSetupResultDto> SyncRemoteHostFirewallAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        RemoteSetupOptionsDto options,
        CancellationToken cancellationToken = default);

    /// <summary>Streams a local image (by tag) to the stack's remote engine via <c>docker save | docker load</c>.</summary>
    Task ShipImageAsync(ManagedStackEntity stack, string imageTag, CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="imageTag"/> is already present on the stack's remote engine.</summary>
    Task<bool> RemoteImageExistsAsync(
        ManagedStackEntity stack,
        string imageTag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Populates a named volume from an uploaded archive stream. External stacks stream the bytes over
    /// SSH into a throwaway work volume on the remote engine, extract there, and copy into the target
    /// volume so the manager never stores the full client (~17 GB) locally.
    /// </summary>
    Task SeedVolumeFromArchiveStreamAsync(
        ManagedStackEntity stack,
        string volumeName,
        Stream archiveStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one file into a named volume by streaming bytes into a throwaway container (no manager
    /// staging directory). Used when extracting unsupported archive formats entry-by-entry.
    /// </summary>
    Task WriteVolumeFileFromStreamAsync(
        ManagedStackEntity stack,
        string volumeName,
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the named volume on the stack's engine (if missing) and populates it with the contents of
    /// a local directory. External stacks stream a tar over SSH; local stacks prefer a fast daemon-side
    /// volume-to-volume copy when the source lives inside the manager's data volume, otherwise a local
    /// tar stream. Existing content in the volume is left in place (files are overwritten, not purged).
    /// </summary>
    Task SeedVolumeAsync(ManagedStackEntity stack, string volumeName, string localSourceDir, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes specific relative paths from within a named volume on the stack's engine (best-effort).
    /// Needed because <see cref="SeedVolumeAsync"/> is additive (it overwrites but never purges), so a
    /// file removed from the local source is not removed from the volume by a re-seed alone. Paths are
    /// relative to the volume root (e.g. <c>Data/patch-G.MPQ</c>); traversal/absolute paths are ignored.
    /// </summary>
    Task DeleteVolumePathsAsync(ManagedStackEntity stack, string volumeName, IEnumerable<string> relativePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds a named volume on the LOCAL daemon (no stack/context) from a local directory. Used for
    /// shared, stack-independent volumes such as the base WoW client. Existing content is overwritten,
    /// not purged.
    /// </summary>
    Task SeedLocalVolumeAsync(string volumeName, string localSourceDir, CancellationToken cancellationToken = default);

    /// <summary>Fetches a named volume on the LOCAL daemon (no stack/context) back into a local directory.</summary>
    Task FetchLocalVolumeAsync(string volumeName, string localDestinationDir, CancellationToken cancellationToken = default);

    /// <summary>Removes a named volume on the LOCAL daemon (no stack/context); best-effort.</summary>
    Task RemoveLocalVolumeAsync(string volumeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the contents of a directory inside a built image into a local directory using
    /// <c>docker create</c> + <c>docker cp</c> on the LOCAL daemon (stack images are built locally before
    /// being shipped to any remote engine, so they are always present locally at build time). Returns
    /// false when the image or path is unavailable; best-effort and does not throw.
    /// </summary>
    Task<bool> ExtractImageDirAsync(string image, string imagePath, string localDestinationDir, CancellationToken cancellationToken = default);

    /// <summary>True when the named volume already exists on the stack's engine (local or remote).</summary>
    Task<bool> VolumeExistsAsync(ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recursively sets ownership of a named volume's contents to <paramref name="uid"/>:<paramref name="gid"/>
    /// on the stack's engine (best-effort). Docker creates named volumes root-owned, but the AzerothCore
    /// services run as a non-root uid and must be able to write their config/logs into the etc/logs
    /// volumes, so those are chowned to the service uid before the stack starts.
    /// </summary>
    Task SetVolumeOwnershipAsync(ManagedStackEntity stack, string volumeName, int uid, int gid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recursively makes a named volume's contents world-readable (<c>chmod -R a+rX</c>) on the stack's
    /// engine (best-effort). Read-only served volumes (e.g. the armory assets served by nginx as a
    /// non-root user) must be readable regardless of the serving container's uid; seeded trees can carry
    /// restrictive source permissions (e.g. 0700 dirs from macOS), which would otherwise 403/deny reads.
    /// </summary>
    Task SetVolumeWorldReadableAsync(ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the contents of a named volume back to a local directory by streaming a tar (the inverse
    /// of <see cref="SeedVolumeAsync"/>). Works for both local and external engines. Used to retrieve
    /// build artifacts (e.g. the launcher exe) or a live baseline (e.g. server DBCs) from a volume.
    /// </summary>
    Task FetchVolumeAsync(ManagedStackEntity stack, string volumeName, string localDestinationDir, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a host-visible file into a path inside a running container (best-effort; no-op when the
    /// container is not running or the source file is missing).
    /// </summary>
    Task CopyFileToContainerAsync(
        ManagedStackEntity stack,
        string containerName,
        string localSourcePath,
        string containerDestinationPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists regular files in a named volume as paths relative to the volume root with byte sizes.
    /// Uses a throwaway read-only container; intended for small managed trees (e.g. client overlay).
    /// </summary>
    Task<IReadOnlyList<VolumeFileEntry>> ListVolumeFilesAsync(
        ManagedStackEntity stack,
        string volumeName,
        CancellationToken cancellationToken = default);

    /// <summary>Lists files in a named volume on the local daemon (no stack context).</summary>
    Task<IReadOnlyList<VolumeFileEntry>> ListLocalVolumeFilesAsync(
        string volumeName,
        CancellationToken cancellationToken = default);

    /// <summary>Lists one directory level inside a volume (paths relative to volume root).</summary>
    Task<IReadOnlyList<VolumeDirectoryEntry>> ListVolumeDirectoryAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>Aggregate stats for a volume tree (file count, total bytes, client markers).</summary>
    Task<VolumeTreeSummary> GetVolumeTreeSummaryAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts files matching <paramref name="filePattern"/> under a volume subdirectory using
    /// BusyBox-safe <c>find</c> (avoids shell glob limits in large folders such as <c>dbc/</c>).
    /// </summary>
    Task<int> CountVolumeFilesAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        string filePattern,
        CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="relativePath"/> exists as a directory inside the volume.</summary>
    Task<bool> VolumeSubdirExistsAsync(
        ManagedStackEntity? stack,
        string volumeName,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>Removes all top-level entries inside a volume (not the volume itself).</summary>
    Task ClearVolumeContentsAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes paths inside a local-daemon volume (no stack context).</summary>
    Task DeleteLocalVolumePathsAsync(
        string volumeName,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the named volume on the target engine when it does not already exist.</summary>
    Task EnsureVolumeExistsAsync(
        ManagedStackEntity? stack,
        string volumeName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the host port a container's internal TCP port is published on (via <c>docker port</c> over
    /// SSH). Returns null when the container is missing or the port is not published.
    /// </summary>
    Task<int?> TryResolveRemotePublishedPortAsync(
        ManagedStackEntity stack,
        string containerName,
        int containerPort,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the host IP and port a container's internal TCP port is published on (via <c>docker port</c>).
    /// </summary>
    Task<(string Host, int Port)?> TryResolveRemotePublishedEndpointAsync(
        ManagedStackEntity stack,
        string containerName,
        int containerPort,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a local <c>127.0.0.1:&lt;port&gt;</c> endpoint that forwards to a management port
    /// (MySQL, SOAP) on an external stack's host over SSH. Keeps MySQL/SOAP off the public internet.
    /// </summary>
    Task<(string Host, int Port)> GetManagementTunnelEndpointAsync(
        ManagedStackEntity stack,
        int remotePort,
        string remoteHost = "127.0.0.1",
        CancellationToken cancellationToken = default);

    /// <summary>Closes an SSH management tunnel so the next request opens a fresh forward.</summary>
    void InvalidateManagementTunnel(ManagedStackEntity stack, int remotePort, string remoteHost = "127.0.0.1");

    /// <summary>Reads ufw status on a Linux VPC host and verifies required allow ports.</summary>
    Task<VpcFirewallStatusDto> ProbeHostFirewallAsync(
        ManagedStackEntity stack,
        VpcSecurityProfileDto profile,
        CancellationToken cancellationToken = default);

    /// <summary>Reads recent SSH auth events from the remote VPC host (auth.log / journald).</summary>
    Task<VpcSshLogsDto> FetchSshAuthLogsAsync(
        ManagedStackEntity stack,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an interactive SSH shell (<c>ssh -tt</c>) for wizard/cloud terminal sessions. Output and
    /// input are raw bytes (PTY stream). The ephemeral ssh config block is removed when the session ends.
    /// </summary>
    Task RunInteractiveShellAsync(
        string host,
        int sshPort,
        string user,
        string privateKey,
        Func<byte[], Task> onOutput,
        ChannelReader<byte[]> input,
        CancellationToken cancellationToken = default);
}

/// <summary>A file or directory inside a Docker named volume (one listing level).</summary>
public sealed class VolumeDirectoryEntry
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public int ItemCount { get; set; }
}

/// <summary>Summary of files stored in a Docker volume.</summary>
public sealed class VolumeTreeSummary
{
    public bool VolumeExists { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public bool HasWowExe { get; set; }
    public bool HasDataMpq { get; set; }
    /// <summary>True when the volume exists but a helper container could not read its contents.</summary>
    public bool InspectionFailed { get; set; }
    public string? InspectionError { get; set; }
}

/// <summary>A file inside a Docker named volume (path relative to volume root).</summary>
public sealed class VolumeFileEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}
