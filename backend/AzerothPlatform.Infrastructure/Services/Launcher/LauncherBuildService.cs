using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Compiles the desktop launcher once on the manager's local engine (docker SDK sidecar, cross-publish
/// to win-x64 single-file) with the global identity baked into <c>launcher.settings.json</c>, then
/// broadcasts the produced exe to every launcher-visible, client-enabled stack's launcher-dist volume
/// (so each stack serves it via its own <c>/launcher/*</c>). Per-stack branding/realmlist/template
/// overrides are delivered at runtime from each stack's portal, layered over the baked global defaults.
/// </summary>
public sealed class LauncherBuildService : ILauncherBuildService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private const int MaxLogLines = 400;

    // Short-lived probe of each stack's /launcher/latest to learn the version it currently serves.
    private static readonly HttpClient VersionProbe = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly LauncherBuildOptions _options;
    private readonly ClientServerOptions _clientServerOptions;
    private readonly ILogger<LauncherBuildService> _logger;

    private readonly object _lock = new();
    private readonly List<string> _log = new();
    private LauncherBuildStatusDto _status = new();
    private Task? _current;

    private readonly string _workPath;
    private readonly string _distPath;

    public LauncherBuildService(
        IServiceScopeFactory scopeFactory,
        IRemoteEngineService remoteEngine,
        IOptions<LauncherBuildOptions> options,
        IOptions<ClientServerOptions> clientServerOptions,
        ILogger<LauncherBuildService> logger)
    {
        _scopeFactory = scopeFactory;
        _remoteEngine = remoteEngine;
        _options = options.Value;
        _clientServerOptions = clientServerOptions.Value;
        _logger = logger;

        _workPath = ResolvePath(_options.WorkPath);
        _distPath = ResolvePath(_options.DistPath);
    }

    private static string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

    public string? GetExecutablePath()
    {
        var exe = Path.Combine(_distPath, _options.ExecutableName);
        return File.Exists(exe) ? exe : null;
    }

    public async Task<LauncherPropagationDto> GetStackVersionsAsync(CancellationToken cancellationToken = default)
    {
        var builtVersion = ReadMetadata()?.Version;

        List<ManagedStackEntity> stacks;
        MigrationOptions migrationOptions;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            migrationOptions = scope.ServiceProvider.GetRequiredService<IOptions<MigrationOptions>>().Value;
            stacks = await db.ManagedStacks
                .Where(s => s.ClientEnabled)
                .OrderBy(s => s.LauncherSortOrder)
                .ToListAsync(cancellationToken);
        }

        var result = new LauncherPropagationDto { BuiltVersion = builtVersion };
        foreach (var stack in stacks)
        {
            var portalUrl = PortalUrlFor(stack, migrationOptions);
            var deployed = await TryReadStackVersionAsync(stack, migrationOptions, cancellationToken);
            result.Stacks.Add(new LauncherStackVersionDto
            {
                StackId = stack.Id,
                StackName = stack.StackName,
                PortalUrl = portalUrl,
                DeployedVersion = deployed,
                Reachable = deployed is not null,
                StatusDetail = await DescribeStackLauncherStatusAsync(
                    stack, portalUrl, deployed, cancellationToken),
                // Up to date only when a build exists and the stack serves exactly that version.
                UpToDate = builtVersion is not null && deployed is not null
                    && CompareVersions(deployed, builtVersion) == 0,
                LauncherVisible = stack.LauncherVisible,
            });
        }

        return result;
    }

    public async Task<LauncherStackVersionDto> ResendToStackAsync(string stackId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_current is { IsCompleted: false })
            {
                throw new InvalidOperationException("A launcher build is currently running. Wait for it to finish before re-sending.");
            }
        }

        var meta = ReadMetadata()
            ?? throw new InvalidOperationException("No launcher build is available yet. Build the launcher first.");

        ManagedStackEntity stack;
        MigrationOptions migrationOptions;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            migrationOptions = scope.ServiceProvider.GetRequiredService<IOptions<MigrationOptions>>().Value;
            stack = await db.ManagedStacks.FirstOrDefaultAsync(s => s.Id == stackId, cancellationToken)
                ?? throw new KeyNotFoundException($"Stack not found: {stackId}");
        }

        if (!stack.ClientEnabled)
        {
            throw new InvalidOperationException("This stack has no client container to serve the launcher.");
        }

        var flavorDir = Path.Combine(_distPath, stack.IncludeArmory ? "with-armory" : "no-armory");
        var flavorExe = Path.Combine(flavorDir, _options.ExecutableName);
        if (!File.Exists(flavorExe))
        {
            flavorExe = Path.Combine(_distPath, _options.ExecutableName);
        }
        if (!File.Exists(flavorExe))
        {
            throw new InvalidOperationException("The built launcher executable is missing. Build the launcher again.");
        }

        var flavorMeta = ReadMetadataFrom(flavorDir) ?? meta;
        await StoreOnStackAsync(
            stack, flavorExe, flavorMeta.Version, flavorMeta.SizeBytes, flavorMeta.Sha256 ?? string.Empty, flavorMeta.BuiltAt, cancellationToken);

        var portalUrl = PortalUrlFor(stack, migrationOptions);
        var deployed = await TryReadStackVersionAsync(stack, migrationOptions, cancellationToken);
        return new LauncherStackVersionDto
        {
            StackId = stack.Id,
            StackName = stack.StackName,
            PortalUrl = portalUrl,
            DeployedVersion = deployed,
            Reachable = deployed is not null,
            StatusDetail = await DescribeStackLauncherStatusAsync(stack, portalUrl, deployed, cancellationToken),
            UpToDate = deployed is not null && CompareVersions(deployed, meta.Version) == 0,
            LauncherVisible = stack.LauncherVisible,
        };
    }

    public Task<LauncherBuildStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(Snapshot());
        }
    }

    public Task<LauncherBuildStatusDto> StartBuildAsync(LauncherVersionPart part, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_current is { IsCompleted: false })
            {
                return Task.FromResult(Snapshot());
            }

            _log.Clear();
            _status = new LauncherBuildStatusDto
            {
                Phase = LauncherBuildPhase.Preparing,
                Message = "Starting launcher build...",
                IsBuilding = true
            };

            _current = Task.Run(() => RunBuildAsync(part, CancellationToken.None));
            return Task.FromResult(Snapshot());
        }
    }

    private async Task RunBuildAsync(LauncherVersionPart part, CancellationToken cancellationToken)
    {
        try
        {
            LauncherDistributionConfigDto config;
            string? iconPath;
            string signingPublicKey;
            // Every client-enabled stack hosts the launcher download at its own /launcher/download, so all
            // of them receive the built executable. LauncherVisible only controls whether a stack appears as
            // a selectable profile in the replicated registry, not whether it can serve the download.
            List<ManagedStackEntity> targetStacks;
            // Each target stack's own portal/client URL, used both to seed a fresh launcher and to read
            // the version it currently serves (so re-initializing the manager can't reset the counter).
            List<string> stackPortalUrls;
            // Portal URL to bake into the launcher as its initial seed: prefer a launcher-visible profile so
            // the launcher opens onto a real profile, falling back to any client stack.
            string? seedPortalUrl;
            using (var scope = _scopeFactory.CreateScope())
            {
                var portal = scope.ServiceProvider.GetRequiredService<ILauncherPortalService>();
                config = await portal.GetConfigAsync(cancellationToken);
                // A global uploaded icon wins; otherwise fall back to the selected style template's icon.
                iconPath = portal.ResolveGlobalAsset(LauncherAssetKind.Icon)?.Path
                    ?? portal.ResolveTemplateAsset(config.Template, "icon")?.Path;

                // The manifest signing pubkey is always baked in so the launcher can verify manifests that
                // arrive over each stack's plain-HTTP channel (trust no longer depends on a manager TLS link).
                signingPublicKey = scope.ServiceProvider
                    .GetRequiredService<IManifestSigningKeyProvider>().PublicKeySpkiBase64;

                var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
                var migrationOptions = scope.ServiceProvider
                    .GetRequiredService<IOptions<MigrationOptions>>().Value;

                targetStacks = await db.ManagedStacks
                    .Where(s => s.ClientEnabled)
                    .OrderBy(s => s.LauncherSortOrder)
                    .ToListAsync(cancellationToken);

                stackPortalUrls = targetStacks
                    .Select(s => PortalUrlFor(s, migrationOptions))
                    .Where(url => url is not null)
                    .Select(url => url!)
                    .ToList();

                // A fresh launcher seeds its known-servers from one stack's portal, then reconciles the whole
                // replicated registry from there (and self-heals to the others), so we only bake in one seed.
                // Prefer a launcher-visible profile; otherwise any client stack still serves the registry.
                var seedStack = targetStacks.FirstOrDefault(s => s.LauncherVisible) ?? targetStacks.FirstOrDefault();
                seedPortalUrl = seedStack is null ? null : PortalUrlFor(seedStack, migrationOptions);
            }

            SetPhase(LauncherBuildPhase.Preparing, "Preparing launcher source...");
            AddLog($"Target stacks: {targetStacks.Count} (client-enabled).");

            // Determine the next version from the highest version ALREADY deployed across the target
            // stacks, falling back to the manager's own last build. The stacks are the source of truth, so
            // dropping/re-initializing the local manager can never reset the counter and ship a build that
            // looks older than what players already run (which would block their self-update).
            var baseline = ReadMetadata()?.Version;
            using (var scope = _scopeFactory.CreateScope())
            {
                var migrationOptions = scope.ServiceProvider
                    .GetRequiredService<IOptions<MigrationOptions>>().Value;
                foreach (var stack in targetStacks)
                {
                    var deployed = await TryReadStackVersionAsync(stack, migrationOptions, cancellationToken);
                    if (deployed is null) { continue; }
                    var portalUrl = PortalUrlFor(stack, migrationOptions);
                    AddLog($"Stack {portalUrl ?? stack.StackName} currently serves launcher v{deployed}.");
                    if (CompareVersions(deployed, baseline) > 0) { baseline = deployed; }
                }
            }

            var version = BumpVersion(baseline, part);
            AddLog($"Launcher version: {version} (bumped {part} from {baseline ?? "0.0.0.0"}).");
            var srcDir = Path.Combine(_workPath, "src");
            PrepareSource(srcDir);
            WriteBakedSettings(srcDir, config, version, seedPortalUrl, signingPublicKey);
            ApplyIcon(srcDir, iconPath);

            SetPhase(LauncherBuildPhase.Publishing, "Compiling launcher flavors locally (with-armory and no-armory)...");
            var withArmoryDir = Path.Combine(_distPath, "with-armory");
            var noArmoryDir = Path.Combine(_distPath, "no-armory");
            var withArmoryExe = await PublishFlavorAsync(srcDir, withArmoryDir, enableArmory: true, cancellationToken);
            var noArmoryExe = await PublishFlavorAsync(srcDir, noArmoryDir, enableArmory: false, cancellationToken);

            var finalExe = Path.Combine(_distPath, _options.ExecutableName);
            File.Copy(withArmoryExe, finalExe, overwrite: true);

            var builtAt = DateTime.UtcNow;
            var size = new FileInfo(finalExe).Length;
            var sha256 = await ComputeSha256Async(finalExe, cancellationToken);
            var metadataJson = JsonSerializer.Serialize(
                new BuildMetadata { Version = version, BuiltAt = builtAt, SizeBytes = size, Sha256 = sha256 },
                JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(_distPath, "build.json"), metadataJson, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(withArmoryDir, "build.json"), metadataJson, cancellationToken);
            var noArmorySize = new FileInfo(noArmoryExe).Length;
            var noArmorySha = await ComputeSha256Async(noArmoryExe, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(noArmoryDir, "build.json"),
                JsonSerializer.Serialize(
                    new BuildMetadata { Version = version, BuiltAt = builtAt, SizeBytes = noArmorySize, Sha256 = noArmorySha },
                    JsonOptions),
                cancellationToken);

            AddLog($"Launcher built: {finalExe} ({size} bytes) plus no-armory flavor.");

            // Broadcast the built exe + a build.json to every target stack's launcher-dist volume (like
            // news distribution) so each stack's own client container serves it at /launcher/latest +
            // /launcher/download - no dependency on the manager for player downloads or self-update.
            // Best-effort per stack: an offline stack is logged and skipped, never failing the whole build.
            if (targetStacks.Count > 0)
            {
                SetPhase(LauncherBuildPhase.Packaging, $"Distributing launcher to {targetStacks.Count} stack(s)...");
                var pushed = 0;
                foreach (var stack in targetStacks)
                {
                    try
                    {
                        var flavorExe = stack.IncludeArmory ? withArmoryExe : noArmoryExe;
                        var flavorSize = stack.IncludeArmory ? size : noArmorySize;
                        var flavorSha = stack.IncludeArmory ? sha256 : noArmorySha;
                        await StoreOnStackAsync(stack, flavorExe, version, flavorSize, flavorSha, builtAt, cancellationToken);
                        pushed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to push launcher to stack {StackId}.", stack.Id);
                        AddLog($"WARN: could not push to stack {stack.StackName}: {ex.Message}");
                    }
                }
                AddLog($"Launcher distributed to {pushed}/{targetStacks.Count} stack(s).");
            }

            SetPhase(LauncherBuildPhase.Completed, "Launcher build completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launcher build failed");
            AddLog($"ERROR: {ex.Message}");
            lock (_lock)
            {
                _status.Phase = LauncherBuildPhase.Failed;
                _status.Message = "Launcher build failed.";
                _status.Error = ex.Message;
                _status.IsBuilding = false;
            }
        }
    }

    /// <summary>
    /// The stack's player-facing client-container base URL (<c>http://{host}:{clientPort}</c>) used for
    /// informational links in the admin UI, or null when the stack has no client container / host.
    /// </summary>
    private static string? PortalUrlFor(ManagedStackEntity stack, MigrationOptions migrationOptions)
    {
        if (!stack.ClientEnabled || stack.ClientPort <= 0)
        {
            return null;
        }

        var host = !string.IsNullOrWhiteSpace(stack.ExternalHost)
            ? RealmlistHostResolver.NormalizeHost(stack.ExternalHost)
            : string.IsNullOrWhiteSpace(stack.RealmlistHostOverride)
                ? RealmlistHostResolver.NormalizeHost(migrationOptions.RealmlistHost)
                : RealmlistHostResolver.NormalizeHost(stack.RealmlistHostOverride);

        return string.IsNullOrWhiteSpace(host)
            ? null
            : $"http://{host.Trim()}:{stack.ClientPort}";
    }

    private static string DescribeStackLauncherStatus(
        ManagedStackEntity stack,
        string? portalUrl,
        string? deployedVersion)
    {
        if (!stack.ClientEnabled)
        {
            return "No client container configured.";
        }

        if (deployedVersion is not null)
        {
            return $"Launcher v{deployedVersion} deployed on stack.";
        }

        if (portalUrl is null)
        {
            return "Client port or public host is not configured.";
        }

        return stack.DeploymentTarget == DeploymentTarget.External
            ? "Launcher not found on stack volume - build the launcher, then Re-send to this stack."
            : "Launcher not found - build the launcher, then Re-send to this stack.";
    }

    private async Task<string> DescribeStackLauncherStatusAsync(
        ManagedStackEntity stack,
        string? portalUrl,
        string? deployedVersion,
        CancellationToken cancellationToken)
    {
        if (deployedVersion is not null)
        {
            return $"Launcher v{deployedVersion} deployed on stack.";
        }

        if (!stack.ClientEnabled)
        {
            return "No client container configured.";
        }

        try
        {
            var volume = DockerComposeOverrideGenerator.ClientLauncherDistVolumeName(stack.Id);
            var files = await _remoteEngine.ListVolumeFilesAsync(stack, volume, cancellationToken);
            if (files.Count > 0)
            {
                return "Launcher volume has files but no valid build.json - use Re-send on the Launcher page.";
            }
        }
        catch (Exception ex)
        {
            AddLog($"Could not inspect launcher volume for stack {stack.StackName}: {ex.Message}");
        }

        return DescribeStackLauncherStatus(stack, portalUrl, deployedVersion);
    }

    /// <summary>
    /// Reads the launcher version on a stack. The launcher-dist volume is checked first (works even when
    /// the client container is stopped); live HTTP against the client container is used as a fallback.
    /// </summary>
    private async Task<string?> TryReadStackVersionAsync(
        ManagedStackEntity stack,
        MigrationOptions migrationOptions,
        CancellationToken cancellationToken)
    {
        if (!stack.ClientEnabled)
        {
            return null;
        }

        var fromVolume = await TryReadStackVersionFromVolumeAsync(stack, cancellationToken);
        if (fromVolume is not null)
        {
            return fromVolume;
        }

        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            return await TryReadStackVersionViaContainerAsync(stack, cancellationToken);
        }

        var portalUrl = PortalUrlFor(stack, migrationOptions);
        return portalUrl is null
            ? null
            : await TryReadStackVersionFromUrlAsync(portalUrl, cancellationToken);
    }

    private async Task<string?> TryReadStackVersionFromVolumeAsync(
        ManagedStackEntity stack,
        CancellationToken cancellationToken)
    {
        try
        {
            var volume = DockerComposeOverrideGenerator.ClientLauncherDistVolumeName(stack.Id);
            var files = await _remoteEngine.ListVolumeFilesAsync(stack, volume, cancellationToken);
            if (files.Count == 0)
            {
                return null;
            }

            var (exitCode, stdout, _) = await _remoteEngine.RunToolInVolumeSubdirAsync(
                stack,
                volume,
                string.Empty,
                "alpine:3.20",
                "cat build.json",
                cancellationToken);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<LauncherBuildManifest>(stdout, ManifestJsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return null;
            }

            var exeName = string.IsNullOrWhiteSpace(manifest.FileName)
                ? _options.ExecutableName
                : manifest.FileName;
            var hasExe = files.Any(f =>
                string.Equals(f.RelativePath, exeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(f.RelativePath), exeName, StringComparison.OrdinalIgnoreCase));

            return hasExe ? manifest.Version : null;
        }
        catch (Exception ex)
        {
            AddLog($"Could not read launcher manifest from stack {stack.StackName} volume: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Best-effort read of the launcher version a stack currently serves (its <c>/launcher/latest</c>).
    /// Returns null when the stack is unreachable or has no build yet, so an offline stack simply doesn't
    /// raise the version baseline.
    /// </summary>
    private async Task<string?> TryReadStackVersionFromUrlAsync(string portalUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await VersionProbe.GetAsync(
                $"{portalUrl.TrimEnd('/')}/launcher/latest", cancellationToken);
            if (!response.IsSuccessStatusCode) { return null; }

            var info = await response.Content.ReadFromJsonAsync<StackLatestInfo>(cancellationToken);
            return string.IsNullOrWhiteSpace(info?.Version) || info?.DownloadAvailable != true
                ? null
                : info!.Version;
        }
        catch (Exception ex)
        {
            AddLog($"Could not read current launcher version from {portalUrl}: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryReadStackVersionViaContainerAsync(
        ManagedStackEntity stack,
        CancellationToken cancellationToken)
    {
        try
        {
            var container = $"{DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName)}-client";
            var args = new List<string>();
            if (stack.DeploymentTarget == DeploymentTarget.External)
            {
                var context = await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
                args.Add("--context");
                args.Add(context);
            }

            args.Add("exec");
            args.Add(container);
            args.Add("curl");
            args.Add("-fsS");
            args.Add($"http://localhost:{_clientServerOptions.ContainerPort}/launcher/latest");

            var (exitCode, stdout, _) = await RunDockerAsync(args, cancellationToken);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            var info = JsonSerializer.Deserialize<StackLatestInfo>(stdout, ManifestJsonOptions);
            return string.IsNullOrWhiteSpace(info?.Version) || info?.DownloadAvailable != true
                ? null
                : info!.Version;
        }
        catch (Exception ex)
        {
            AddLog($"Could not read launcher version from stack {stack.StackName} client container: {ex.Message}");
            return null;
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunDockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Stages the built exe + a <see cref="LauncherBuildManifest"/> (build.json) into a temp dir and seeds
    /// it into the target stack's launcher-dist volume (engine-aware). The stack's client container mounts
    /// that volume and serves the artifact to launchers.
    /// </summary>
    private async Task StoreOnStackAsync(
        ManagedStackEntity stack, string exePath, string version, long size, string sha256, DateTime builtAt,
        CancellationToken cancellationToken)
    {
        AddLog($"Storing launcher on stack {stack.StackName}'s launcher-dist volume...");
        var stageDir = Path.Combine(_workPath, $"launcher-dist-{stack.Id}");
        if (Directory.Exists(stageDir)) { Directory.Delete(stageDir, recursive: true); }
        Directory.CreateDirectory(stageDir);

        try
        {
            var exeName = _options.ExecutableName;
            File.Copy(exePath, Path.Combine(stageDir, exeName), overwrite: true);

            var manifest = new LauncherBuildManifest
            {
                Version = version,
                FileName = exeName,
                SizeBytes = size,
                Sha256 = sha256,
                BuiltAt = builtAt,
            };
            await File.WriteAllTextAsync(
                Path.Combine(stageDir, "build.json"),
                JsonSerializer.Serialize(manifest, ManifestJsonOptions),
                cancellationToken);

            var volume = DockerComposeOverrideGenerator.ClientLauncherDistVolumeName(stack.Id);
            await _remoteEngine.SeedVolumeAsync(stack, volume, stageDir, cancellationToken);
            await _remoteEngine.SetVolumeWorldReadableAsync(stack, volume, cancellationToken);
            AddLog($"Launcher stored on stack {stack.StackName} (volume {volume}).");
        }
        finally
        {
            try { Directory.Delete(stageDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private void PrepareSource(string srcDir)
    {
        if (!Directory.Exists(_options.SourcePath))
        {
            throw new InvalidOperationException(
                $"Launcher source not found at '{_options.SourcePath}'. Ensure the Dockerfile copies launcher/ into the image.");
        }

        if (Directory.Exists(srcDir)) { Directory.Delete(srcDir, recursive: true); }
        Directory.CreateDirectory(srcDir);
        CopyDirectory(_options.SourcePath, srcDir);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or ".git") { continue; }
            var relative = Path.GetRelativePath(source, dir);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || relative.StartsWith("bin" + Path.DirectorySeparatorChar)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || relative.StartsWith("bin" + Path.DirectorySeparatorChar)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private void WriteBakedSettings(
        string srcDir,
        LauncherDistributionConfigDto config,
        string version,
        string? seedPortalUrl,
        string signingPublicKey)
    {
        // The launcher reads launcher.settings.json from AppContext.BaseDirectory (bundled into the
        // single-file via IncludeAllContentForSelfExtract). Bake the GLOBAL defaults so friends need no
        // setup. The baked ServerUrl seeds the launcher at one stack's own portal container; from there it
        // reconciles the full replicated registry and per-stack overrides (branding/realmlist/template)
        // are applied at runtime from each stack's portal. The manifest signing pubkey is baked in so
        // manifest trust no longer depends on a manager TLS channel.
        var serverUrl = !string.IsNullOrWhiteSpace(seedPortalUrl)
            ? seedPortalUrl.Trim()
            : (string.IsNullOrWhiteSpace(config.PublicBaseUrl) ? null : config.PublicBaseUrl.Trim());

        var settings = new BakedLauncherSettings
        {
            ServerUrl = serverUrl,
            SigningPublicKey = string.IsNullOrWhiteSpace(signingPublicKey) ? null : signingPublicKey,
            BrandingTitle = string.IsNullOrWhiteSpace(config.BrandingTitle) ? null : config.BrandingTitle,
            DefaultInstallSubfolder = string.IsNullOrWhiteSpace(config.AppName) ? null : config.AppName,
            AppName = string.IsNullOrWhiteSpace(config.AppName) ? null : config.AppName,
            MultiProfile = true,
            RequireLogin = config.RequireLogin,
            Version = version
        };

        var projectDir = Path.GetDirectoryName(Path.Combine(srcDir, _options.ProjectRelativePath))!;
        Directory.CreateDirectory(projectDir);
        var path = Path.Combine(projectDir, "launcher.settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        AddLog($"Baked identity into launcher.settings.json (server={settings.ServerUrl}, app={settings.DefaultInstallSubfolder}).");
    }

    /// <summary>
    /// Copies the global app icon into the project as <c>AppIcon.ico</c> so the csproj (which
    /// conditionally sets ApplicationIcon and bundles it) bakes it as the Windows exe icon and the
    /// launcher can load it as the window/taskbar icon at runtime. No icon uploaded → nothing to do.
    /// </summary>
    private void ApplyIcon(string srcDir, string? iconPath)
    {
        var projectDir = Path.GetDirectoryName(Path.Combine(srcDir, _options.ProjectRelativePath))!;
        var target = Path.Combine(projectDir, "AppIcon.ico");

        // Clear any stale icon from a previous build so removing the icon takes effect.
        if (File.Exists(target)) { File.Delete(target); }

        if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
        {
            AddLog("No app icon configured; using the default launcher icon.");
            return;
        }

        Directory.CreateDirectory(projectDir);
        File.Copy(iconPath, target, overwrite: true);
        AddLog("Baked global app icon (AppIcon.ico) into the launcher.");
    }

    /// <summary>
    /// Compiles the launcher on the local daemon via the same seed/run/fetch volume recipe as
    /// <see cref="PublishOnRemoteAsync"/> (just without a <c>--context</c>): seed the prepared source into
    /// a build volume, run the SDK sidecar publishing into an output volume, then fetch the produced
    /// artifacts back to the local publish dir. This avoids any host bind mount so it works with the
    /// manager's data living in a named volume.
    /// </summary>
    private async Task<string> PublishFlavorAsync(
        string srcDir,
        string destDir,
        bool enableArmory,
        CancellationToken cancellationToken)
    {
        var publishDir = Path.Combine(_distPath, enableArmory ? "publish-with-armory" : "publish-no-armory");
        if (Directory.Exists(publishDir)) { Directory.Delete(publishDir, recursive: true); }
        Directory.CreateDirectory(publishDir);
        await PublishAsync(srcDir, publishDir, enableArmory, cancellationToken);

        var producedExe = Path.Combine(publishDir, _options.ExecutableName);
        if (!File.Exists(producedExe))
        {
            producedExe = Directory.EnumerateFiles(publishDir, "*.exe").FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"Publish did not produce an .exe for the {(enableArmory ? "with-armory" : "no-armory")} flavor.");
        }

        if (Directory.Exists(destDir)) { Directory.Delete(destDir, recursive: true); }
        Directory.CreateDirectory(destDir);
        var destExe = Path.Combine(destDir, _options.ExecutableName);
        File.Copy(producedExe, destExe, overwrite: true);
        try { Directory.Delete(publishDir, recursive: true); } catch { /* best effort cleanup */ }
        return destExe;
    }

    private async Task PublishAsync(string srcDir, string publishDir, bool enableArmory, CancellationToken cancellationToken)
    {
        const string srcVolume = "acore-launcher-src";
        const string outVolume = "acore-launcher-out";

        AddLog($"Seeding launcher source to the local engine ({(enableArmory ? "with-armory" : "no-armory")})...");
        // Reset both volumes so a previous build's artifacts/source can't leak into this one.
        await _remoteEngine.RemoveLocalVolumeAsync(outVolume, cancellationToken);
        await _remoteEngine.RemoveLocalVolumeAsync(srcVolume, cancellationToken);
        await _remoteEngine.SeedLocalVolumeAsync(srcVolume, srcDir, cancellationToken);

        var armoryProperty = enableArmory ? "true" : "false";
        var innerCommand =
            $"cd /src && dotnet publish {_options.ProjectRelativePath} -c Release -r win-x64 " +
            $"-p:PublishSingleFile=true -p:AZP_ENABLE_ARMORY={armoryProperty} --self-contained true -o /out";
        var arguments =
            $"run --rm -v {srcVolume}:/src -v {outVolume}:/out {_options.SdkImage} " +
            $"sh -c \"{innerCommand}\"";

        AddLog($"docker {arguments}");
        var exitCode = await RunProcessAsync("docker", arguments, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"docker publish exited with code {exitCode}.");
        }

        AddLog("Fetching built launcher from the local engine...");
        await _remoteEngine.FetchLocalVolumeAsync(outVolume, publishDir, cancellationToken);

        // Best-effort cleanup of the build volumes.
        await _remoteEngine.RemoveLocalVolumeAsync(outVolume, cancellationToken);
        await _remoteEngine.RemoveLocalVolumeAsync(srcVolume, cancellationToken);
    }

    private async Task<int> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
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
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { AddLog(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { AddLog(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private void SetPhase(LauncherBuildPhase phase, string message)
    {
        lock (_lock)
        {
            _status.Phase = phase;
            _status.Message = message;
            _status.IsBuilding = phase is not (LauncherBuildPhase.Completed or LauncherBuildPhase.Failed);
        }

        AddLog(message);
    }

    private void AddLog(string line)
    {
        lock (_lock)
        {
            _log.Add($"[{DateTime.UtcNow:HH:mm:ss}] {line}");
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
        }
    }

    private LauncherBuildStatusDto Snapshot()
    {
        var meta = ReadMetadata();
        var exe = Path.Combine(_distPath, _options.ExecutableName);
        return new LauncherBuildStatusDto
        {
            Phase = _status.Phase,
            Message = _status.Message,
            IsBuilding = _status.IsBuilding,
            Error = _status.Error,
            Log = new List<string>(_log),
            AvailableVersion = meta?.Version,
            AvailableBuiltAt = meta?.BuiltAt,
            AvailableSizeBytes = meta?.SizeBytes ?? 0,
            AvailableSha256 = meta?.Sha256,
            DownloadAvailable = File.Exists(exe)
        };
    }

    /// <summary>
    /// Increments the requested segment of a <c>Release.Update.Minor.Patch</c> version and resets all
    /// less-significant segments to zero. A missing or non-semantic previous version is treated as
    /// <c>0.0.0.0</c>, so the first semantic build starts a fresh scheme (patch -> 0.0.0.1, release -> 1.0.0.0).
    /// </summary>
    internal static string BumpVersion(string? previous, LauncherVersionPart part)
    {
        var segments = ParseVersion(previous);

        var index = (int)part; // Release=0 .. Patch=3
        segments[index]++;
        for (var i = index + 1; i < 4; i++)
        {
            segments[i] = 0;
        }

        return string.Join('.', segments);
    }

    /// <summary>
    /// Parses a <c>Release.Update.Minor.Patch</c> version into four segments. A missing or non-semantic
    /// value parses to <c>0.0.0.0</c>.
    /// </summary>
    private static int[] ParseVersion(string? version)
    {
        var segments = new int[4];
        if (!string.IsNullOrWhiteSpace(version) && version.Contains('.'))
        {
            var parts = version.Split('.');
            for (var i = 0; i < 4 && i < parts.Length; i++)
            {
                int.TryParse(parts[i], out segments[i]);
            }
        }

        return segments;
    }

    /// <summary>Compares two versions segment by segment; positive when <paramref name="a"/> is newer.</summary>
    internal static int CompareVersions(string? a, string? b)
    {
        var pa = ParseVersion(a);
        var pb = ParseVersion(b);
        for (var i = 0; i < 4; i++)
        {
            if (pa[i] != pb[i]) { return pa[i].CompareTo(pb[i]); }
        }

        return 0;
    }

    private sealed class StackLatestInfo
    {
        public string? Version { get; set; }
        public bool DownloadAvailable { get; set; }
    }

    private BuildMetadata? ReadMetadata() => ReadMetadataFrom(_distPath);

    private BuildMetadata? ReadMetadataFrom(string directory)
    {
        var metaPath = Path.Combine(directory, "build.json");
        if (!File.Exists(metaPath)) { return null; }
        try
        {
            return JsonSerializer.Deserialize<BuildMetadata>(File.ReadAllText(metaPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private sealed class BuildMetadata
    {
        public string Version { get; set; } = string.Empty;
        public DateTime BuiltAt { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class BakedLauncherSettings
    {
        public string? ServerUrl { get; set; }
        public string? SigningPublicKey { get; set; }
        public string? BrandingTitle { get; set; }
        public string? AppName { get; set; }
        public string? DefaultInstallSubfolder { get; set; }
        public string? DefaultInstallDirectory { get; set; }
        public bool MultiProfile { get; set; }
        public bool RequireLogin { get; set; }
        public string? Version { get; set; }
    }
}
