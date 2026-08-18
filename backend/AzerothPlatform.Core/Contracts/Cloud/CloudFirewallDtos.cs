namespace AzerothPlatform.Core.Contracts;

public sealed class SyncCloudSecurityGroupRequestDto
{
    /// <summary>Linked cloud account to use (AWS supported for automation today).</summary>
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>Admin CIDR for SSH (e.g. 203.0.113.10/32). Replaces template your-ip/32.</summary>
    public string AdminSourceCidr { get; set; } = string.Empty;

    /// <summary>Optional EC2 instance id when known (skips IP lookup).</summary>
    public string? InstanceId { get; set; }

    /// <summary>Optional AWS region when instance id is supplied.</summary>
    public string? Region { get; set; }
}

public sealed class CloudFirewallApplyResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public CloudProvider Provider { get; set; }

    public int RulesApplied { get; set; }

    public int RulesSkipped { get; set; }

    public IReadOnlyList<string> SecurityGroupIds { get; set; } = [];
}
