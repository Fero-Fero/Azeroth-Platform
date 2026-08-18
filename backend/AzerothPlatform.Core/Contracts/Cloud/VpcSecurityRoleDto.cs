namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Describes a network security role on an external VPC (what is exposed, who configures it, and whether
/// Docker already enforces isolation/bind addresses).
/// </summary>
public class VpcSecurityRoleDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>public, vpc, or manager-only</summary>
    public string Exposure { get; set; } = string.Empty;

    /// <summary>Whether host ufw should allow these ports.</summary>
    public bool HostFirewall { get; set; }

    /// <summary>Whether the cloud security group must allow these ports.</summary>
    public bool CloudSecurityGroup { get; set; }

    /// <summary>Docker publish/bind policy handles exposure for this role.</summary>
    public bool DockerHandlesBind { get; set; }

    /// <summary>Where administrators change settings in the dashboard (empty when automatic).</summary>
    public string AdminSettingsLocation { get; set; } = string.Empty;

    /// <summary>Default or example ports (informational).</summary>
    public List<int> DefaultPorts { get; set; } = new();
}
