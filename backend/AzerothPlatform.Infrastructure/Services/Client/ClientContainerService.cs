using System.Diagnostics;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Drives the per-stack <c>azeroth-platform-client</c> container's mutating endpoints by running
/// <c>docker [--context] exec {container} sh -c 'curl ... $CLIENT_AUTH_TOKEN'</c> against loopback
/// <c>/rescan</c> and <c>/force-verify</c>. Using the container's own env token avoids 401s when the
/// DB session secret was never persisted or drifted from compose. Execing from inside the container
/// makes no assumptions about manager-to-container networking and works for external stacks via the
/// stack's docker context.
/// </summary>
public sealed class ClientContainerService : IClientContainerService
{
    private static readonly JsonSerializerOptions StatusJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string ManifestStatusCachePrefix = "client-manifest-status:";

    /// <summary>
    /// Short enough that the Client tab stays live, long enough that its 3-second poll during an install
    /// does not spawn a <c>docker exec</c> per request.
    /// </summary>
    private static readonly TimeSpan ManifestStatusCacheTtl = TimeSpan.FromSeconds(5);

    private readonly AzerothCoreDbContext _dbContext;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly IMemoryCache _cache;
    private readonly ClientServerOptions _options;
    private readonly ILogger<ClientContainerService> _logger;

    public ClientContainerService(
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine,
        IMemoryCache cache,
        IOptions<ClientServerOptions> options,
        ILogger<ClientContainerService> logger)
    {
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public Task<bool> RescanAsync(string stackId, CancellationToken cancellationToken = default) =>
        InvokeAsync(stackId, "rescan", cancellationToken);

    public Task<bool> ForceVerifyAsync(string stackId, CancellationToken cancellationToken = default) =>
        InvokeAsync(stackId, "force-verify", cancellationToken);

    public async Task<ClientManifestStatus?> GetManifestStatusAsync(
        string stackId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{ManifestStatusCachePrefix}{stackId}";
        if (refresh)
        {
            _cache.Remove(cacheKey);
        }
        else if (_cache.TryGetValue<ClientManifestStatus?>(cacheKey, out var cached))
        {
            return cached;
        }

        var status = await ReadManifestStatusAsync(stackId, cancellationToken);
        _cache.Set(cacheKey, status, ManifestStatusCacheTtl);
        return status;
    }

    private async Task<ClientManifestStatus?> ReadManifestStatusAsync(
        string stackId, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        if (!stack.ClientEnabled)
        {
            return null;
        }

        try
        {
            var container = $"{DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName)}-client";
            string? context = null;
            if (stack.DeploymentTarget == DeploymentTarget.External)
            {
                context = await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
            }

            var args = BuildLoopbackReadCurlArgs(context, container, "manifest-status", _options.ContainerPort);
            var (exitCode, stdout, stderr) = await RunAsync("docker", args, (byte[]?)null, cancellationToken);
            if (exitCode != 0)
            {
                _logger.LogDebug(
                    "Could not read the client manifest status for stack {StackId}: {Detail}",
                    stackId, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
                return null;
            }

            return JsonSerializer.Deserialize<ClientManifestStatus>(stdout, StatusJsonOptions);
        }
        catch (Exception ex)
        {
            // A stopped or unreachable container is an ordinary state here, so this stays a debug log and
            // a null result; the caller renders "unknown" rather than failing the whole info request.
            _logger.LogDebug(ex, "Could not read the client manifest status for stack {StackId}.", stackId);
            return null;
        }
    }

    public async Task<bool> PushPortalAsync(string stackId, string portalJson, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        if (!stack.ClientEnabled)
        {
            _logger.LogInformation("Stack {StackId} has no client container; skipping portal push.", stackId);
            return false;
        }

        var container = $"{DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName)}-client";

        // Write the JSON verbatim into the container's cache volume via stdin so no shell quoting mangles
        // it (the container reads /client/cache/portal.json fresh on each GET /portal).
        var args = new List<string>();
        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            var context = await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
            args.Add("--context");
            args.Add(context);
        }

        args.Add("exec");
        args.Add("-i");
        args.Add(container);
        args.Add("sh");
        args.Add("-c");
        args.Add("cat > /client/cache/portal.json");

        var (exitCode, stdout, stderr) = await RunAsync("docker", args, portalJson, cancellationToken);
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Portal push failed: {detail.Trim()}");
        }

        _logger.LogInformation("Pushed portal.json to stack {StackId}.", stackId);
        return true;
    }

    public async Task<bool> PushBrandingAsync(
        string stackId, string assetName, byte[]? content, CancellationToken cancellationToken = default)
    {
        if (assetName is not ("background" or "logo"))
        {
            throw new ArgumentOutOfRangeException(nameof(assetName), assetName, "Branding asset must be 'background' or 'logo'.");
        }

        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        if (!stack.ClientEnabled)
        {
            _logger.LogInformation("Stack {StackId} has no client container; skipping branding push.", stackId);
            return false;
        }

        var container = $"{DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName)}-client";
        var target = $"/client/cache/branding/{assetName}";

        var args = new List<string>();
        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            var context = await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
            args.Add("--context");
            args.Add(context);
        }

        args.Add("exec");
        args.Add("-i");
        args.Add(container);
        args.Add("sh");
        args.Add("-c");
        // Store branding at a fixed extension-less path; the container sniffs the content type on serve.
        args.Add(content is null
            ? $"rm -f {target}"
            : $"mkdir -p /client/cache/branding && cat > {target}");

        var (exitCode, stdout, stderr) = await RunAsync("docker", args, content, cancellationToken);
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Branding push failed: {detail.Trim()}");
        }

        _logger.LogInformation(
            "{Action} branding '{Asset}' for stack {StackId}.",
            content is null ? "Cleared" : "Pushed", assetName, stackId);
        return true;
    }

    public async Task<bool> PushNewsAsync(
        string stackId,
        string? newsJson,
        IReadOnlyDictionary<string, byte[]> coverImages,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        if (!stack.ClientEnabled)
        {
            _logger.LogInformation("Stack {StackId} has no client container; skipping news push.", stackId);
            return false;
        }

        var container = $"{DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName)}-client";
        string? context = null;
        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            context = await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
        }

        // No news: wipe the whole news dir so /news falls back to "[]" and stale covers are removed.
        if (string.IsNullOrWhiteSpace(newsJson))
        {
            await ExecShellAsync(context, container, "rm -rf /client/cache/news", stdin: (byte[]?)null, cancellationToken);
            _logger.LogInformation("Cleared launcher news for stack {StackId}.", stackId);
            return true;
        }

        // Replace the feed atomically-ish: clear the dir, recreate it, then stream news.json in via stdin.
        // Clearing first guarantees no cover from a removed article lingers on the container.
        await ExecShellAsync(
            context, container,
            "rm -rf /client/cache/news && mkdir -p /client/cache/news && cat > /client/cache/news/news.json",
            System.Text.Encoding.UTF8.GetBytes(newsJson), cancellationToken);

        var pushedImages = 0;
        foreach (var (itemId, bytes) in coverImages)
        {
            // The id is interpolated into the shell command, so only allow a safe filename charset.
            if (!IsSafeNewsImageId(itemId) || bytes.Length == 0)
            {
                continue;
            }

            await ExecShellAsync(
                context, container,
                $"cat > /client/cache/news/{itemId}",
                bytes, cancellationToken);
            pushedImages++;
        }

        _logger.LogInformation(
            "Pushed launcher news for stack {StackId} ({Images} cover images).", stackId, pushedImages);
        return true;
    }

    private static bool IsSafeNewsImageId(string itemId) =>
        !string.IsNullOrWhiteSpace(itemId)
        && itemId.Length <= 128
        && itemId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && !itemId.Contains("..", StringComparison.Ordinal);

    /// <summary>Runs <c>docker [--context c] exec -i {container} sh -c "{command}"</c>, optionally piping stdin.</summary>
    private async Task ExecShellAsync(
        string? context, string container, string command, byte[]? stdin, CancellationToken cancellationToken)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(context))
        {
            args.Add("--context");
            args.Add(context);
        }

        args.Add("exec");
        args.Add("-i");
        args.Add(container);
        args.Add("sh");
        args.Add("-c");
        args.Add(command);

        var (exitCode, stdout, stderr) = await RunAsync("docker", args, stdin, cancellationToken);
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"News push failed: {detail.Trim()}");
        }
    }

    private async Task<bool> InvokeAsync(string stackId, string endpoint, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        if (!stack.ClientEnabled)
        {
            _logger.LogInformation("Stack {StackId} has no client container; skipping {Endpoint}.", stackId, endpoint);
            return false;
        }

        var container = $"{DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName)}-client";
        string? context = null;
        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            context = await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
        }

        var args = BuildLoopbackMutatingCurlArgs(context, container, endpoint, _options.ContainerPort);

        var (exitCode, stdout, stderr) = await RunAsync("docker", args, (byte[]?)null, cancellationToken);
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Client-server {endpoint} failed: {detail.Trim()}");
        }

        _logger.LogInformation("Client-server {Endpoint} completed for stack {StackId}.", endpoint, stackId);
        return true;
    }

    /// <summary>
    /// Builds <c>docker [--context] exec {container} sh -c 'curl ... $CLIENT_AUTH_TOKEN'</c>.
    /// Auth uses the container's own env so it matches even when the DB session secret was never
    /// persisted (client rendered without armory) or drifted from a throwaway compose token. The
    /// curl command is a single argv to <c>sh -c</c> (no host-side token interpolation), which
    /// avoids mangling a Bearer header that contains a space.
    /// </summary>
    public static List<string> BuildLoopbackMutatingCurlArgs(
        string? dockerContext, string container, string endpoint, int port)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(dockerContext))
        {
            args.Add("--context");
            args.Add(dockerContext);
        }

        args.Add("exec");
        args.Add(container);
        args.Add("sh");
        args.Add("-c");
        args.Add(
            $"curl -fsS -X POST -H \"Authorization: Bearer ${{CLIENT_AUTH_TOKEN}}\" http://localhost:{port}/{endpoint}");
        return args;
    }

    /// <summary>
    /// As <see cref="BuildLoopbackMutatingCurlArgs"/> but a plain GET with no auth header, for the
    /// endpoints that are already public (they expose nothing <c>/manifest</c> does not). Short timeouts
    /// keep a wedged container from stalling a Client tab load.
    /// </summary>
    public static List<string> BuildLoopbackReadCurlArgs(
        string? dockerContext, string container, string endpoint, int port)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(dockerContext))
        {
            args.Add("--context");
            args.Add(dockerContext);
        }

        args.Add("exec");
        args.Add(container);
        args.Add("sh");
        args.Add("-c");
        args.Add($"curl -fsS --max-time 10 http://localhost:{port}/{endpoint}");
        return args;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IEnumerable<string> arguments, string? stdin, CancellationToken cancellationToken) =>
        RunAsync(fileName, arguments, stdin is null ? null : System.Text.Encoding.UTF8.GetBytes(stdin), cancellationToken);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IEnumerable<string> arguments, byte[]? stdin, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        process.Start();
        if (stdin is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
