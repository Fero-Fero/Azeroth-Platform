using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class CloudSetupDialogService : ICloudSetupDialogService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly ICloudLaunchService _cloudLaunchService;
    private readonly IGcpCredentialResolver _gcpCredentialResolver;
    private readonly GcpComputeClient _gcpComputeClient;
    private readonly IAzureCredentialResolver _azureCredentialResolver;
    private readonly AzureComputeClient _azureComputeClient;

    public CloudSetupDialogService(
        AzerothCoreDbContext dbContext,
        ICloudLaunchService cloudLaunchService,
        IGcpCredentialResolver gcpCredentialResolver,
        GcpComputeClient gcpComputeClient,
        IAzureCredentialResolver azureCredentialResolver,
        AzureComputeClient azureComputeClient)
    {
        _dbContext = dbContext;
        _cloudLaunchService = cloudLaunchService;
        _gcpCredentialResolver = gcpCredentialResolver;
        _gcpComputeClient = gcpComputeClient;
        _azureCredentialResolver = azureCredentialResolver;
        _azureComputeClient = azureComputeClient;
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

        IReadOnlyList<CloudLaunchCatalogOptionDto> projects = [];
        var defaultProjectId = string.IsNullOrWhiteSpace(entity.DefaultProjectId) ? null : entity.DefaultProjectId;
        if (provider == CloudProvider.Gcp)
        {
            try
            {
                var access = await _gcpCredentialResolver.ResolveAsync(entity, cancellationToken);
                var listed = await _gcpComputeClient.ListProjectsAsync(access, cancellationToken);
                projects = listed
                    .Select(project => new CloudLaunchCatalogOptionDto
                    {
                        Value = project.Value,
                        Label = project.Label,
                        Description = project.Description,
                    })
                    .ToList();
                if (string.IsNullOrWhiteSpace(defaultProjectId) && !string.IsNullOrWhiteSpace(access.ProjectId))
                {
                    defaultProjectId = access.ProjectId;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                projects = [];
            }
        }
        else if (provider == CloudProvider.Azure)
        {
            try
            {
                var access = await _azureCredentialResolver.ResolveAsync(entity, cancellationToken);
                var listed = await _azureComputeClient.ListSubscriptionsAsync(access, cancellationToken);
                projects = listed
                    .Select(subscription => new CloudLaunchCatalogOptionDto
                    {
                        Value = subscription.Value,
                        Label = subscription.Label,
                        Description = subscription.Description,
                    })
                    .ToList();
                if (string.IsNullOrWhiteSpace(defaultProjectId) && !string.IsNullOrWhiteSpace(access.SubscriptionId))
                {
                    defaultProjectId = access.SubscriptionId;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                projects = [];
            }
        }

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
            CanSyncFirewall = provider is CloudProvider.Aws
                or CloudProvider.DigitalOcean
                or CloudProvider.Vultr
                or CloudProvider.Gcp
                or CloudProvider.Azure
                or CloudProvider.Hetzner,
            AutoFirewallDefault = true,
            SuggestedAdminCidr = null,
            LaunchDefaults = defaults,
            DefaultProjectId = defaultProjectId,
            Projects = projects,
        };
    }
}
