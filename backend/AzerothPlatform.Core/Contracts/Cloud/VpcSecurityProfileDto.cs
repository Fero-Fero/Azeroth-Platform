namespace AzerothPlatform.Core.Contracts;

/// <summary>Suggested firewall and cloud rules for a specific external deployment or stack.</summary>
public class VpcSecurityProfileDto
{
    public string Host { get; set; } = string.Empty;

    public List<VpcSecurityRuleDto> HostFirewallRules { get; set; } = new();

    public List<VpcSecurityRuleDto> CloudSecurityGroupRules { get; set; } = new();

    public List<VpcSecurityRuleDto> DeniedPorts { get; set; } = new();

    public string Notes { get; set; } = string.Empty;
}

/// <summary>A single allow/deny rule suggestion.</summary>
public class VpcSecurityRuleDto
{
    public string RoleId { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Protocol { get; set; } = "tcp";

    public string Action { get; set; } = "allow";

    public string Source { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}
