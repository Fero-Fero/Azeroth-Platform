using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Background service that periodically checks all stacks for available updates
/// </summary>
public sealed class StackUpdateCheckerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StackUpdateCheckerService> _logger;
    private readonly TimeSpan _checkInterval;
    private readonly bool _enabled;
    private readonly bool _checkOnStartup;
    private readonly TimeSpan _startupDelay;

    public StackUpdateCheckerService(
        IServiceScopeFactory scopeFactory,
        IOptions<StackUpdateCheckerOptions> options,
        ILogger<StackUpdateCheckerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        
        var opts = options.Value;
        _checkInterval = TimeSpan.FromMinutes(opts.CheckIntervalMinutes);
        _enabled = opts.Enabled;
        _checkOnStartup = opts.CheckOnStartup;
        _startupDelay = TimeSpan.FromSeconds(opts.DelayStartupSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Stack update checker is disabled");
            return;
        }

        _logger.LogInformation("Stack update checker started (interval: {Interval})", _checkInterval);

        try
        {
            // Optional startup delay to let system initialize
            if (_startupDelay > TimeSpan.Zero)
            {
                _logger.LogInformation("Delaying startup check for {Delay}", _startupDelay);
                await Task.Delay(_startupDelay, stoppingToken);
            }

            // Check immediately on startup if configured
            if (_checkOnStartup)
            {
                await CheckAllStacksAsync(stoppingToken);
            }

            // Then check periodically
            using var timer = new PeriodicTimer(_checkInterval);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(stoppingToken);
                    await CheckAllStacksAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Service is stopping
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in stack update checker timer loop");
                }
            }

            _logger.LogInformation("Stack update checker stopped");
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation during shutdown or startup
            _logger.LogInformation("Stack update checker cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in stack update checker");
            throw;
        }
    }

    private async Task CheckAllStacksAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            var versionService = scope.ServiceProvider.GetRequiredService<IStackVersionService>();

            var stacks = await dbContext.ManagedStacks
                .Where(s => s.Status != StackStatus.Building) // Skip stacks currently building
                .ToListAsync(cancellationToken);

            if (stacks.Count == 0)
            {
                _logger.LogDebug("No stacks to check for updates");
                return;
            }

            // Deployment-drift detection: flag built stacks whose generated runtime artifacts (.env /
            // docker-compose.override.yml) predate the current manager template. These need to be
            // re-applied (restart / re-apply regenerates the artifacts) to pick up current fixes such as
            // the data-plane host-bind policy. Surfaced to the UI via StackUpdateStatusDto; logged here so
            // it's visible to operators at startup without opening the UI.
            var driftedStacks = stacks
                .Where(s => !string.IsNullOrEmpty(s.CoreCommitSha)
                            && s.RuntimeArtifactVersion < RuntimeArtifactTemplate.CurrentVersion)
                .ToList();
            if (driftedStacks.Count > 0)
            {
                _logger.LogWarning(
                    "{Count} stack(s) have out-of-date runtime configuration (template v{Required}) and " +
                    "should be re-applied (restart/re-apply to regenerate .env + docker-compose.override.yml): {Stacks}",
                    driftedStacks.Count,
                    RuntimeArtifactTemplate.CurrentVersion,
                    string.Join(", ", driftedStacks.Select(s => $"{s.StackName} (v{s.RuntimeArtifactVersion})")));
            }

            _logger.LogInformation("Checking updates for {Count} stack(s)", stacks.Count);
            
            var startTime = DateTime.UtcNow;
            var successCount = 0;
            var failCount = 0;

            foreach (var stack in stacks)
            {
                // Skip stacks that have never been built
                if (string.IsNullOrEmpty(stack.CoreCommitSha))
                {
                    _logger.LogDebug("Skipping stack {StackId} - never built", stack.Id);
                    continue;
                }

                try
                {
                    await versionService.CheckAndCacheStatusAsync(stack.Id, cancellationToken);
                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to check updates for stack {StackId} ({StackName})", 
                        stack.Id, stack.StackName);
                    failCount++;
                    // Continue to next stack
                }
            }

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("Update check completed in {Duration:N1}s: {Success} successful, {Fail} failed",
                duration.TotalSeconds, successCount, failCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in stack update checker");
        }
    }
}
