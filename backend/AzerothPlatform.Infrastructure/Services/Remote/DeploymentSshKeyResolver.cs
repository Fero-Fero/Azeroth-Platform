using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>Resolves SSH private key material from inline PEM or a saved vault entry.</summary>
public static class DeploymentSshKeyResolver
{
    public static async Task<string> ResolvePrivateKeyAsync(
        DeploymentConfigDto deployment,
        ICloudSshKeyService cloudSshKeyService,
        string? usageContext = null,
        CancellationToken cancellationToken = default)
    {
        deployment ??= new DeploymentConfigDto();

        if (!string.IsNullOrWhiteSpace(deployment.ExternalSshPrivateKey))
        {
            return deployment.ExternalSshPrivateKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(deployment.SavedSshKeyId))
        {
            return await cloudSshKeyService.ResolvePrivateKeyAsync(
                deployment.SavedSshKeyId.Trim(),
                usageContext,
                cancellationToken);
        }

        throw new InvalidOperationException("SSH private key is required (paste a key or select a saved key).");
    }

    public static bool HasResolvableKey(DeploymentConfigDto deployment)
    {
        deployment ??= new DeploymentConfigDto();
        return !string.IsNullOrWhiteSpace(deployment.ExternalSshPrivateKey)
               || !string.IsNullOrWhiteSpace(deployment.SavedSshKeyId);
    }
}
