using System.Diagnostics;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Drives the per-stack <c>azeroth-platform-client</c> container's mutating endpoints by running
/// <c>docker [--context] exec {container} curl ... localhost/rescan|force-verify</c>. Execing from
/// inside the container makes no assumptions about manager-to-container networking and works for
/// external stacks via the stack's docker context.
/// </summary>
public sealed class ClientContainerService : IClientContainerService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly ClientServerOptions _options;
    private readonly ILogger<ClientContainerService> _logger;

    public ClientContainerService(
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine,
        IOptions<ClientServerOptions> options,
        ILogger<ClientContainerService> logger)
    {
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
        _options = options.Value;
        _logger = logger;
    }

    public Task<bool> RescanAsync(string stackId, CancellationToken cancellationToken = default) =>
        InvokeAsync(stackId, "rescan", cancellationToken);

    public Task<bool> ForceVerifyAsync(string stackId, CancellationToken cancellationToken = default) =>
        InvokeAsync(stackId, "force-verify", cancellationToken);

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

        // Build argv explicitly (no shell): each element is passed verbatim, so the bearer header (which
        // contains a space) and the URL survive intact. A single quoted/escaped command string would be
        // re-tokenized by the OS + `sh -c`, which mangled the arguments (curl reported "no URL specified").
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
        args.Add("-X");
        args.Add("POST");
        if (!string.IsNullOrEmpty(stack.ArmorySessionSecret))
        {
            args.Add("-H");
            args.Add($"Authorization: Bearer {stack.ArmorySessionSecret}");
        }
        args.Add($"http://localhost:{_options.ContainerPort}/{endpoint}");

        var (exitCode, stdout, stderr) = await RunAsync("docker", args, (byte[]?)null, cancellationToken);
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Client-server {endpoint} failed: {detail.Trim()}");
        }

        _logger.LogInformation("Client-server {Endpoint} completed for stack {StackId}.", endpoint, stackId);
        return true;
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
