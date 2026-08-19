using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.DbcStore;

/// <summary>Kicks a DBC store sync on API start when the store is empty (skipped in tests).</summary>
public sealed class DbcBaselineStoreHostedService : BackgroundService
{
    private readonly IDbcBaselineStore _store;
    private readonly IModuleInstallHookRunner _hooks;
    private readonly MigrationOptions _options;
    private readonly ILogger<DbcBaselineStoreHostedService> _logger;

    public DbcBaselineStoreHostedService(
        IDbcBaselineStore store,
        IModuleInstallHookRunner hooks,
        IOptions<MigrationOptions> options,
        ILogger<DbcBaselineStoreHostedService> logger)
    {
        _store = store;
        _hooks = hooks;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _hooks.All.Count;
        if (!_options.DbcStoreAutoSyncOnStart)
        {
            _logger.LogInformation("DBC baseline auto-sync on start is disabled.");
            return Task.CompletedTask;
        }

        if (_store.IsReady())
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("DBC baseline store is empty; starting background sync.");
        _store.EnqueueSync(force: false);
        return Task.CompletedTask;
    }
}
