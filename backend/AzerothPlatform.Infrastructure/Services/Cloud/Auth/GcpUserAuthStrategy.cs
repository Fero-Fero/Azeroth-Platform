using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class GcpUserAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly CloudOAuthOptions _options;

    public GcpUserAuthStrategy(IOptions<CloudOAuthOptions> options)
    {
        _options = options.Value;
    }

    public override CloudProvider Provider => CloudProvider.Gcp;

    public override CloudAuthProviderStatusDto GetStatus()
    {
        var configured = _options.Gcp.IsConfigured;
        return new CloudAuthProviderStatusDto
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.OAuth,
            IsConfigured = configured,
            IsImplemented = false,
            SupportsPkce = true,
            SignInLabel = "Sign in with Google Cloud",
            UnavailableReason = configured
                ? "Google Cloud user OAuth is not enabled yet. Use Advanced to paste a service account JSON key."
                : "Google Cloud OAuth is not configured. Set CloudOAuth:Gcp:ClientId and ClientSecret, or use Advanced to paste a service account JSON key.",
        };
    }
}
