using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class AzureEntraAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly CloudOAuthOptions _options;

    public AzureEntraAuthStrategy(IOptions<CloudOAuthOptions> options)
    {
        _options = options.Value;
    }

    public override CloudProvider Provider => CloudProvider.Azure;

    public override CloudAuthProviderStatusDto GetStatus()
    {
        var configured = _options.Azure.IsConfigured;
        return new CloudAuthProviderStatusDto
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.OAuth,
            IsConfigured = configured,
            IsImplemented = false,
            SupportsPkce = true,
            SignInLabel = "Sign in with Microsoft",
            UnavailableReason = configured
                ? "Azure Entra sign-in is not enabled yet. Use Advanced to paste a service principal."
                : "Azure OAuth is not configured. Set CloudOAuth:Azure:ClientId and ClientSecret, or use Advanced to paste a service principal.",
        };
    }
}
