namespace AzerothPlatform.Core.Contracts;

public enum CloudProvider
{
    DigitalOcean = 0,
    Aws = 1,
    Gcp = 2,
    Azure = 3,
    Hetzner = 4,
    Vultr = 5,
}

public sealed class CloudProviderConnectionDto
{
    public string Id { get; set; } = string.Empty;

    public CloudProvider Provider { get; set; }

    public string Label { get; set; } = string.Empty;

    public string? DefaultRegion { get; set; }

    /// <summary>GCP project id for user OAuth connections (and SA JSON when known).</summary>
    public string? DefaultProjectId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public CloudAuthMethod AuthMethod { get; set; } = CloudAuthMethod.Manual;

    /// <summary>Masked account hint (email, account id, subscription name). Never a secret.</summary>
    public string? AccountHint { get; set; }

    public DateTime? TokenExpiresAtUtc { get; set; }

    public bool NeedsReauth { get; set; }
}

public sealed class CloudConnectionVerifyResultDto
{
    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;

    public CloudProviderConnectionDto Connection { get; set; } = new();
}

public sealed class CreateCloudProviderConnectionRequestDto
{
    public CloudProvider Provider { get; set; } = CloudProvider.DigitalOcean;

    public string Label { get; set; } = string.Empty;

    /// <summary>DigitalOcean, Hetzner, or Vultr API token (write-only; stored encrypted).</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>AWS IAM access key ID (write-only; stored encrypted).</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>AWS IAM secret access key (write-only; stored encrypted).</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>GCP service account JSON key file contents (write-only; stored encrypted).</summary>
    public string ServiceAccountJson { get; set; } = string.Empty;

    /// <summary>Azure AD tenant ID (write-only; stored encrypted with other Azure fields).</summary>
    public string AzureTenantId { get; set; } = string.Empty;

    /// <summary>Azure service principal application (client) ID.</summary>
    public string AzureClientId { get; set; } = string.Empty;

    /// <summary>Azure service principal client secret.</summary>
    public string AzureClientSecret { get; set; } = string.Empty;

    /// <summary>Azure subscription ID.</summary>
    public string AzureSubscriptionId { get; set; } = string.Empty;

    /// <summary>Optional default region/zone filter (AWS region, GCP zone/region prefix, Azure location).</summary>
    public string? DefaultRegion { get; set; }
}

public sealed class UpsertCloudOAuthConnectionRequestDto
{
    public CloudProvider Provider { get; set; }

    public string Label { get; set; } = string.Empty;

    public string AccountHint { get; set; } = string.Empty;

    public string ProtectedCredentials { get; set; } = string.Empty;

    public DateTime? TokenExpiresAtUtc { get; set; }

    public string? ReconnectConnectionId { get; set; }

    public string? DefaultRegion { get; set; }

    public string? DefaultProjectId { get; set; }

    public CloudAuthMethod AuthMethod { get; set; } = CloudAuthMethod.OAuth;
}

public sealed class CloudInstanceDto
{
    public string Id { get; set; } = string.Empty;

    public CloudProvider Provider { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string PublicHost { get; set; } = string.Empty;

    public string SuggestedSshUser { get; set; } = "ubuntu";

    public string Image { get; set; } = string.Empty;

    /// <summary>Provider size / instance type (t3.micro, s-2vcpu-2gb, cx22, …).</summary>
    public string InstanceType { get; set; } = string.Empty;
}
