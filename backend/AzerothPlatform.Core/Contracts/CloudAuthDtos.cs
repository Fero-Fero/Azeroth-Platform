namespace AzerothPlatform.Core.Contracts;

public enum CloudAuthMethod
{
    Manual = 0,
    OAuth = 1,
    AssumedRole = 2,
}

public enum CloudLoginMode
{
    OAuth = 0,
    DeviceCode = 1,
    GuidedToken = 2,
    ManualOnly = 3,
    AssumedRole = 4,
}

public sealed class CloudAuthProviderStatusDto
{
    public CloudProvider Provider { get; set; }

    public CloudLoginMode LoginMode { get; set; }

    /// <summary>True when OAuth/OIDC client credentials are present in configuration.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>True when this provider's interactive login flow is implemented (not just planned).</summary>
    public bool IsImplemented { get; set; }

    public bool SupportsPkce { get; set; }

    public string SignInLabel { get; set; } = "Sign in";

    /// <summary>Shown when Sign in is unavailable; empty when the button can start a flow.</summary>
    public string UnavailableReason { get; set; } = string.Empty;
}

public sealed class CloudAuthStartRequestDto
{
    /// <summary>Optional frontend path or absolute URL to return to after the callback.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>When set, the callback updates this connection instead of creating a new one.</summary>
    public string? ReconnectConnectionId { get; set; }

    public string? Label { get; set; }

    /// <summary>AWS policy tier when starting a Connect AWS account flow: ReadOnly, Standard, or Full.</summary>
    public string? PolicyTier { get; set; }

    /// <summary>Reuse an External ID already shown in the Connect AWS wizard (tier change / reconnect).</summary>
    public string? ExternalId { get; set; }
}

public sealed class CloudAuthStartResultDto
{
    public string? AuthorizationUrl { get; set; }

    public string? State { get; set; }

    public string? DeviceCode { get; set; }

    public string? VerificationUri { get; set; }

    public string? UserCode { get; set; }

    public int? IntervalSeconds { get; set; }

    /// <summary>True when the operator should paste a token/keys instead of a browser redirect.</summary>
    public bool RequiresManualCredentials { get; set; }

    public string? Message { get; set; }

    /// <summary>AWS cross-account connect: unique External ID for the trust policy.</summary>
    public string? ExternalId { get; set; }

    public string? CloudFormationConsoleUrl { get; set; }

    public IReadOnlyList<CloudAuthAwsTemplateDto>? AwsTemplates { get; set; }
}

public sealed class CloudAuthAwsTemplateDto
{
    public string PolicyTier { get; set; } = "Full";

    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string CloudFormationYaml { get; set; } = string.Empty;
}

public sealed class CloudAuthCompleteRequestDto
{
    public string? RoleArn { get; set; }

    public string? ExternalId { get; set; }

    public string? Label { get; set; }

    public string? ReconnectConnectionId { get; set; }

    public string? DefaultRegion { get; set; }
}

public sealed class CloudInstanceSetupDialogDto
{
    public string ConnectionId { get; set; } = string.Empty;

    public CloudProvider Provider { get; set; }

    public string Label { get; set; } = string.Empty;

    public CloudAuthMethod AuthMethod { get; set; }

    public string? AccountHint { get; set; }

    public bool CanList { get; set; }

    public bool CanCreate { get; set; }

    public bool CanBootstrapExisting { get; set; }

    public bool CanSyncFirewall { get; set; }

    public bool AutoFirewallDefault { get; set; } = true;

    public string? SuggestedAdminCidr { get; set; }

    public CloudLaunchDefaultsDto? LaunchDefaults { get; set; }
}
