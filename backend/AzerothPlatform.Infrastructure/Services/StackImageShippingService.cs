using System.Diagnostics;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <inheritdoc />
public sealed class StackImageShippingService : IStackImageShippingService
{
    private static readonly string[] AcoreRepositories =
    [
        "acore/ac-wotlk-worldserver",
        "acore/ac-wotlk-authserver",
        "acore/ac-wotlk-db-import",
        "acore/ac-wotlk-client-data",
    ];

    private readonly IRemoteEngineService _remoteEngine;
    private readonly IArmoryImageService _armoryImageService;
    private readonly IClientServerImageService _clientImageService;
    private readonly string _clientImageName;
    private readonly ILogger<StackImageShippingService> _logger;

    public StackImageShippingService(
        IRemoteEngineService remoteEngine,
        IArmoryImageService armoryImageService,
        IClientServerImageService clientImageService,
        IOptions<ClientServerOptions> clientServerOptions,
        ILogger<StackImageShippingService> logger)
    {
        _remoteEngine = remoteEngine;
        _armoryImageService = armoryImageService;
        _clientImageService = clientImageService;
        _clientImageName = clientServerOptions.Value.ImageName;
        _logger = logger;
    }

    public async Task ShipStackImagesAsync(
        ManagedStackEntity stack,
        bool includeArmory,
        bool includeClient,
        CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            return;
        }

        try
        {
            if (includeArmory)
            {
                await _armoryImageService.EnsureImageAsync(stack.Id, cancellationToken);
            }

            if (includeClient)
            {
                await _clientImageService.EnsureImageAsync(dockerContext: null, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure local images before shipping for stack {StackId}", stack.Id);
        }

        var tags = AcoreRepositories
            .Select(repository => $"{repository}:{stack.Id}")
            .ToList();

        if (includeArmory)
        {
            tags.Add(_armoryImageService.ImageNameFor(stack.Id));
        }

        if (includeClient && !string.IsNullOrWhiteSpace(_clientImageName))
        {
            tags.Add(_clientImageName);
        }

        foreach (var tag in tags)
        {
            try
            {
                var localTag = await EnsureLocalImageTagAsync(tag, cancellationToken);
                if (await _remoteEngine.RemoteImageExistsAsync(stack, localTag, cancellationToken))
                {
                    _logger.LogInformation(
                        "Image {Image} already exists on remote engine for stack {StackId}; skipping ship.",
                        localTag,
                        stack.Id);
                    continue;
                }

                _logger.LogInformation("Shipping image {Image} to remote engine for stack {StackId}.", localTag, stack.Id);
                await _remoteEngine.ShipImageAsync(stack, localTag, cancellationToken);
                await EnsureRemoteImageTagAsync(stack, localTag, tag, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ship image {Image} to remote for stack {StackId}", tag, stack.Id);
            }
        }
    }

    /// <summary>
    /// AzerothCore compose builds tag images as <c>localhost/acore/…</c>; external overrides reference
    /// <c>acore/…</c>. Ensure the canonical tag exists locally before <c>docker save</c>.
    /// </summary>
    private static async Task<string> EnsureLocalImageTagAsync(string canonicalTag, CancellationToken cancellationToken)
    {
        if (await LocalImageExistsAsync(canonicalTag, cancellationToken))
        {
            return canonicalTag;
        }

        var slash = canonicalTag.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            throw new InvalidOperationException($"Local image '{canonicalTag}' was not found.");
        }

        var localhostTag = $"localhost/{canonicalTag}";
        if (!await LocalImageExistsAsync(localhostTag, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Local image '{canonicalTag}' (or '{localhostTag}') was not found. Build the stack first.");
        }

        var (exitCode, _, stderr) = await RunDockerAsync($"tag {Quote(localhostTag)} {Quote(canonicalTag)}", cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to tag {localhostTag} as {canonicalTag}: {stderr}");
        }

        return canonicalTag;
    }

    /// <summary>
    /// After <c>docker load</c>, the remote may only have the saved reference; retag when needed.
    /// </summary>
    private async Task EnsureRemoteImageTagAsync(
        ManagedStackEntity stack,
        string loadedTag,
        string canonicalTag,
        CancellationToken cancellationToken)
    {
        if (string.Equals(loadedTag, canonicalTag, StringComparison.Ordinal))
        {
            return;
        }

        var contextName = await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
        var (exitCode, _, stderr) = await RunDockerAsync(
            $"--context {contextName} tag {Quote(loadedTag)} {Quote(canonicalTag)}",
            cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to retag remote image {loadedTag} as {canonicalTag}: {stderr}");
        }
    }

    private static async Task<bool> LocalImageExistsAsync(string imageTag, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunDockerAsync($"images -q {Quote(imageTag)}", cancellationToken);
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunDockerAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start docker process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
