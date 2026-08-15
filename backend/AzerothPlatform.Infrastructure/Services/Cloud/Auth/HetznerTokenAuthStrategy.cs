using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class HetznerTokenAuthStrategy : CloudProviderAuthStrategyBase
{
    public override CloudProvider Provider => CloudProvider.Hetzner;

    public override CloudAuthProviderStatusDto GetStatus()
        => new()
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.GuidedToken,
            IsConfigured = true,
            IsImplemented = true,
            SupportsPkce = false,
            SignInLabel = "Connect with token",
            UnavailableReason = string.Empty,
        };
}
