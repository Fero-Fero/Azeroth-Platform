using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class DigitalOceanAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly CloudOAuthOptions _options;

    public DigitalOceanAuthStrategy(IOptions<CloudOAuthOptions> options)
    {
        _options = options.Value;
    }

    public override CloudProvider Provider => CloudProvider.DigitalOcean;

    public override CloudAuthProviderStatusDto GetStatus()
    {
        var configured = _options.DigitalOcean.IsConfigured;
        return new CloudAuthProviderStatusDto
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.OAuth,
            IsConfigured = configured,
            IsImplemented = false,
            SupportsPkce = false,
            SignInLabel = "Sign in with DigitalOcean",
            UnavailableReason = configured
                ? "DigitalOcean OAuth is not enabled yet. Use Advanced to paste an API token."
                : "DigitalOcean OAuth is not configured. Set CloudOAuth:DigitalOcean:ClientId and ClientSecret, or use Advanced to paste an API token.",
        };
    }
}
