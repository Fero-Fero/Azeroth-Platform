using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class CloudSetupDialogService : ICloudSetupDialogService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly ICloudLaunchService _cloudLaunchService;

    public CloudSetupDialogService(
        AzerothCoreDbContext dbContext,
        ICloudLaunchService cloudLaunchService)
    {
        _dbContext = dbContext;
        _cloudLaunchService = cloudLaunchService;
    }

    public async Task<CloudInstanceSetupDialogDto> GetAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections.AsNoTracking()
                         .FirstOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");

        if (!Enum.TryParse<CloudProvider>(entity.Provider, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException("Unknown cloud provider on this connection.");
        }

        var defaults = await _cloudLaunchService.GetDefaultsAsync(connectionId, cancellationToken);
        var authMethod = Enum.TryParse<CloudAuthMethod>(entity.AuthMethod, ignoreCase: true, out var parsedMethod)
            ? parsedMethod
            : CloudAuthMethod.Manual;

        return new CloudInstanceSetupDialogDto
        {
            ConnectionId = entity.Id,
            Provider = provider,
            Label = entity.Label,
            AuthMethod = authMethod,
            AccountHint = string.IsNullOrWhiteSpace(entity.AccountHint) ? null : entity.AccountHint,
            CanList = true,
            CanCreate = defaults.SupportsCreate,
            CanBootstrapExisting = defaults.SupportsBootstrapExisting,
            CanSyncFirewall = provider == CloudProvider.Aws,
            AutoFirewallDefault = true,
            SuggestedAdminCidr = null,
            LaunchDefaults = defaults,
        };
    }
}