namespace AzerothPlatform.Core.Contracts;

/// <summary>Options for first-time remote VPC provisioning.</summary>
public class RemoteSetupOptionsDto
{
    public RemoteHostOs RemoteOs { get; set; } = RemoteHostOs.Linux;

    /// <summary>Configure host <c>ufw</c> (Linux only).</summary>
    public bool EnableHostFirewall { get; set; } = true;

    /// <summary>Install and enable unattended security upgrades (Linux only).</summary>
    public bool EnableUnattendedUpgrades { get; set; } = true;

    public int AuthServerPort { get; set; } = 3724;

    public int WorldServerPort { get; set; } = 8085;

    /// <summary>Host port opened on the host firewall during setup or sync.</summary>
    public int ArmoryPort { get; set; } = StackNetworkDefaults.DefaultArmoryPort;

    /// <summary>Host port opened on the host firewall during setup or sync.</summary>
    public int ClientPort { get; set; } = StackNetworkDefaults.DefaultClientPort;

    public int SshPort { get; set; } = 22;
}
