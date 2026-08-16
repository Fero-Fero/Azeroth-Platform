namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Upsert an unfinished VPC stack from the create-stack wizard after a cloud instance exists.
/// </summary>
public sealed class StackSetupDraftRequestDto
{
    /// <summary>Existing draft stack to update. When empty, a matching cloud instance draft is reused.</summary>
    public string? StackId { get; set; }

    /// <summary>Wizard step id to resume on (e.g. <c>server-config</c>).</summary>
    public string WizardStepId { get; set; } = "deployment";

    /// <summary>JSON snapshot of the wizard form (SSH private key is stripped before storage).</summary>
    public string WizardDraftJson { get; set; } = "{}";

    /// <summary>Optional display name from the Server step. Placeholder used until then.</summary>
    public string? StackName { get; set; }

    public DeploymentConfigDto Deployment { get; set; } = new();
}

/// <summary>Payload used to resume the create-stack wizard for a <see cref="StackStatus.SetupIncomplete"/> stack.</summary>
public sealed class StackSetupDraftDto
{
    public string StackId { get; set; } = string.Empty;

    public string StackName { get; set; } = string.Empty;

    public string WizardStepId { get; set; } = "deployment";

    public string WizardDraftJson { get; set; } = "{}";

    /// <summary>Decrypted SSH private key for Verify VPC. Empty when a vaulted key id is used.</summary>
    public string ExternalSshPrivateKey { get; set; } = string.Empty;

    public DeploymentConfigDto Deployment { get; set; } = new();
}
