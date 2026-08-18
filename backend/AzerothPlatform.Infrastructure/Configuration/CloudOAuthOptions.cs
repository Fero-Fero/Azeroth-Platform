using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Platform-registered OAuth apps for cloud provider sign-in. Leave client ids blank on air-gapped
/// installs - operators then use Advanced credential paste only.
/// </summary>
public sealed class CloudOAuthOptions
{
    public const string SectionName = "CloudOAuth";

    /// <summary>Admin SPA origin used after the OAuth callback (dev: Vite, prod: same-origin API host).</summary>
    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";

    public string FrontendCallbackPath { get; set; } = "/admin/cloud/oauth-callback";

    /// <summary>
    /// Public API base used as the OAuth <c>redirect_uri</c> prefix. Blank means derive from the
    /// incoming request (scheme + host).
    /// </summary>
    public string PublicApiBaseUrl { get; set; } = string.Empty;

    public CloudOAuthProviderOptions DigitalOcean { get; set; } = new();

    public CloudOAuthProviderOptions Vultr { get; set; } = new();

    public CloudOAuthProviderOptions Gcp { get; set; } = new();

    public CloudOAuthProviderOptions Azure { get; set; } = new();

    public CloudAwsAuthOptions Aws { get; set; } = new();
}

public sealed class CloudAwsAuthOptions
{
    /// <summary>12-digit AWS account id that customer roles must trust (the platform account).</summary>
    public string PlatformAccountId { get; set; } = string.Empty;

    /// <summary>
    /// Optional IAM user keys used only to call <c>sts:AssumeRole</c>. Blank uses the default AWS
    /// credential chain (environment, shared profile, or instance profile).
    /// </summary>
    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public bool IsConfigured =>
        PlatformAccountId.Trim().Length == 12 && PlatformAccountId.Trim().All(char.IsDigit);
}

public sealed class CloudOAuthProviderOptions
{
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Optional override of the default <c>/api/cloud/auth/{provider}/callback</c> URI.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>Azure Entra tenant id (<c>organizations</c> when blank).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Vultr OIDC provider id used at the token endpoint.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Optional Vultr authorize URL. Blank uses OIDC discovery, then a provider-id fallback.</summary>
    public string AuthorizeUrl { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool IsVultrOAuthConfigured =>
        IsConfigured && !string.IsNullOrWhiteSpace(ProviderId);
}

internal static class CloudOAuthRedirectUri
{
    public static string Resolve(
        CloudOAuthOptions options,
        CloudOAuthProviderOptions providerOptions,
        CloudProvider provider,
        string? requestCallbackBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(providerOptions.RedirectUri))
        {
            return providerOptions.RedirectUri.Trim();
        }

        var path = $"/api/cloud/auth/{provider}/callback";
        if (!string.IsNullOrWhiteSpace(options.PublicApiBaseUrl))
        {
            return options.PublicApiBaseUrl.TrimEnd('/') + path;
        }

        var origin = (requestCallbackBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(origin))
        {
            throw new InvalidOperationException(
                $"Set CloudOAuth:{provider}:RedirectUri or CloudOAuth:PublicApiBaseUrl so the OAuth callback URL is known.");
        }

        return origin + path;
    }
}