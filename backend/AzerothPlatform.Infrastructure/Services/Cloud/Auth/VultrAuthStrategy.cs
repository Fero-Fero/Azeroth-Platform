using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class VultrAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly CloudOAuthOptions _options;

    public VultrAuthStrategy(IOptions<CloudOAuthOptions> options)
    {
        _options = options.Value;
    }

    public override CloudProvider Provider => CloudProvider.Vultr;

    public override CloudAuthProviderStatusDto GetStatus()
    {
        var configured = _options.Vultr.IsConfigured;
        return new CloudAuthProviderStatusDto
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.OAuth,
            IsConfigured = configured,
            IsImplemented = false,
            SupportsPkce = true,
            SignInLabel = "Sign in with Vultr",
            UnavailableReason = configured
                ? "Vultr OAuth is not enabled yet. Use Advanced to paste an API key."
                : "Vultr OAuth is not configured. Set CloudOAuth:Vultr:ClientId and ClientSecret, or use Advanced to paste an API key.",
        };
    }
}
