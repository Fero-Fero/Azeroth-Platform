namespace AzerothPlatform.Core.Contracts;

/// <summary>Live verification of host firewall and Docker bind policy on an external VPC.</summary>
public class VpcFirewallStatusDto
{
    public bool OverallHealthy { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool UfwInstalled { get; set; }

    public bool UfwActive { get; set; }

    public string? UfwStatusSummary { get; set; }

    /// <summary><c>ufw</c> or <c>windows</c>.</summary>
    public string FirewallProduct { get; set; } = "ufw";

    public List<VpcSecurityCheckDto> Checks { get; set; } = new();
}

public class VpcSecurityCheckDto
{
    /// <summary>host-firewall, docker-bind, or cloud-sg</summary>
    public string Category { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string RoleId { get; set; } = string.Empty;

    public int? Port { get; set; }

    /// <summary>ok, warning, error, unknown, not-applicable</summary>
    public string Status { get; set; } = "unknown";

    public string Message { get; set; } = string.Empty;
}
