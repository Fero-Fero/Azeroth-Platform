using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.DbcStore;

/// <summary>Prepares the DBC baseline store at startup. Conversion of DBC CSVs happens on demand.</summary>
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
        _ = _options.DbcStoreAutoSyncOnStart;
        _ = _store.IsReady();
        _logger.LogInformation(
            "DBC baselines convert on demand from each stack's data directory when a patch or module needs a table.");
        return Task.CompletedTask;
    }
}
