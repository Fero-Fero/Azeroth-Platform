namespace AzerothPlatform.Core.Contracts;

/// <summary>Static catalog of external VPC security roles.</summary>
public static class VpcSecurityCatalog
{
    public const string RoleAdmin = "admin";
    public const string RolePlayer = "player";
    public const string RoleWeb = "web";
    public const string RoleManagement = "management";

    public static VpcSecurityCatalogDto CreateCatalog()
        => new()
        {
            Roles =
            [
                new VpcSecurityRoleDto
                {
                    Id = RoleAdmin,
                    Name = "Admin / SSH",
                    Description = "Platform SSH access to install Docker and manage the remote engine.",
                    Exposure = "manager-only",
                    HostFirewall = true,
                    CloudSecurityGroup = true,
                    DockerHandlesBind = false,
                    AdminSettingsLocation = "Create Stack → Deployment (SSH credentials)",
                    DefaultPorts = [22]
                },
                new VpcSecurityRoleDto
                {
                    Id = RolePlayer,
                    Name = "Player / Game",
                    Description = "WoW authentication and world protocol ports players connect to directly.",
                    Exposure = "public",
                    HostFirewall = true,
                    CloudSecurityGroup = true,
                    DockerHandlesBind = true,
                    AdminSettingsLocation = "Create Stack → Ports (auth & world server ports)",
                    DefaultPorts = [3724, 8085]
                },
                new VpcSecurityRoleDto
                {
                    Id = RoleWeb,
                    Name = "Player / Web",
                    Description = "Armory website and client patch download HTTP services.",
                    Exposure = "public",
                    HostFirewall = true,
                    CloudSecurityGroup = true,
                    DockerHandlesBind = true,
                    AdminSettingsLocation = "Stack Overview → Armory & client web access",
                    DefaultPorts = [StackNetworkDefaults.DefaultArmoryPort, StackNetworkDefaults.DefaultClientPort]
                },
                new VpcSecurityRoleDto
                {
                    Id = RoleManagement,
                    Name = "Management / Data plane",
                    Description = "MySQL and worldserver SOAP for manager automation - never expose to the internet.",
                    Exposure = "manager-only",
                    HostFirewall = false,
                    CloudSecurityGroup = false,
                    DockerHandlesBind = true,
                    AdminSettingsLocation = "Automatic (Docker bind on external host IP)",
                    DefaultPorts = [3306, 7878]
                }
            ]
        };

    public static VpcSecurityProfileDto BuildProfile(
        string host,
        int authPort,
        int worldPort,
        int? armoryPort,
        int? clientPort,
        int databasePort,
        int soapPort,
        int sshPort = 22)
    {
        armoryPort ??= StackNetworkDefaults.DefaultArmoryPort;
        clientPort ??= StackNetworkDefaults.DefaultClientPort;

        var profile = new VpcSecurityProfileDto
        {
            Host = host,
            Notes = "Management ports (MySQL, SOAP) are VPC-only via Docker bind. Do not open them in your cloud security group."
        };

        profile.HostFirewallRules.Add(new VpcSecurityRuleDto
        {
            RoleId = RoleAdmin,
            Port = sshPort,
            Description = "SSH"
        });
        profile.HostFirewallRules.Add(new VpcSecurityRuleDto
        {
            RoleId = RolePlayer,
            Port = authPort,
            Description = "Authserver"
        });
        profile.HostFirewallRules.Add(new VpcSecurityRuleDto
        {
            RoleId = RolePlayer,
            Port = worldPort,
            Description = "Worldserver"
        });

        profile.CloudSecurityGroupRules.Add(new VpcSecurityRuleDto
        {
            RoleId = RoleAdmin,
            Port = sshPort,
            Source = "your-ip/32",
            Description = "SSH - restrict to your IP only"
        });
        profile.CloudSecurityGroupRules.Add(new VpcSecurityRuleDto
        {
            RoleId = RolePlayer,
            Port = authPort,
            Source = "0.0.0.0/0",
            Description = "Authserver - players"
        });
        profile.CloudSecurityGroupRules.Add(new VpcSecurityRuleDto
        {
            RoleId = RolePlayer,
            Port = worldPort,
            Source = "0.0.0.0/0",
            Description = "Worldserver - players"
        });

        if (armoryPort is > 0)
        {
            var rule = new VpcSecurityRuleDto
            {
                RoleId = RoleWeb,
                Port = armoryPort.Value,
                Description = "Armory website"
            };
            profile.HostFirewallRules.Add(rule);
            profile.CloudSecurityGroupRules.Add(new VpcSecurityRuleDto
            {
                RoleId = rule.RoleId,
                Port = rule.Port,
                Source = "0.0.0.0/0",
                Description = rule.Description
            });
        }

        if (clientPort is > 0)
        {
            var rule = new VpcSecurityRuleDto
            {
                RoleId = RoleWeb,
                Port = clientPort.Value,
                Description = "Client file server (launcher patches)"
            };
            profile.HostFirewallRules.Add(rule);
            profile.CloudSecurityGroupRules.Add(new VpcSecurityRuleDto
            {
                RoleId = rule.RoleId,
                Port = rule.Port,
                Source = "0.0.0.0/0",
                Description = rule.Description
            });
        }

        profile.DeniedPorts.Add(new VpcSecurityRuleDto
        {
            RoleId = RoleManagement,
            Port = databasePort,
            Action = "deny",
            Description = "MySQL - manager/VPC only (Docker bind)"
        });
        profile.DeniedPorts.Add(new VpcSecurityRuleDto
        {
            RoleId = RoleManagement,
            Port = soapPort,
            Action = "deny",
            Description = "SOAP - manager/VPC only (Docker bind)"
        });

        return profile;
    }

    /// <summary>
    /// Default ingress used when launching a new VM, before stack ports are chosen.
    /// SSH uses <paramref name="adminSourceCidr"/> when provided; otherwise 0.0.0.0/0.
    /// </summary>
    public static IReadOnlyList<VpcSecurityRuleDto> BuildLaunchCloudIngressRules(string? adminSourceCidr)
    {
        var sshCidr = string.IsNullOrWhiteSpace(adminSourceCidr) ? "0.0.0.0/0" : adminSourceCidr.Trim();
        var profile = BuildProfile(
            host: string.Empty,
            authPort: 3724,
            worldPort: 8085,
            armoryPort: StackNetworkDefaults.DefaultArmoryPort,
            clientPort: StackNetworkDefaults.DefaultClientPort,
            databasePort: 3306,
            soapPort: 7878);
        foreach (var rule in profile.CloudSecurityGroupRules)
        {
            if (string.Equals(rule.Source, "your-ip/32", StringComparison.OrdinalIgnoreCase))
            {
                rule.Source = sshCidr;
            }
        }

        return profile.CloudSecurityGroupRules;
    }

    /// <summary>
    /// True when SSH was launched with a pinned admin IP but the probe did not receive that CIDR,
    /// so expected SSH is the fallback <c>0.0.0.0/0</c>.
    /// </summary>
    public static bool IsUnpinnedAdminSsh(VpcSecurityRuleDto rule)
    {
        if (!string.Equals(rule.RoleId, RoleAdmin, StringComparison.Ordinal))
        {
            return false;
        }

        var source = (rule.Source ?? string.Empty).Trim();
        return string.IsNullOrEmpty(source)
               || source is "0.0.0.0/0" or "::/0" or "your-ip/32";
    }

    /// <summary>
    /// Launch pins SSH to the admin IP when known. Verify VPC often omits that CIDR, so any
    /// tcp/22 source (including a /32) satisfies unpinned admin SSH. Player/web ports still
    /// require a public CIDR or an exact match.
    /// </summary>
    public static bool ProbeIngressSourceSatisfied(string expectedSource, string actualCidr, bool adminSshUnpinned)
    {
        var actual = (actualCidr ?? string.Empty).Trim();
        var expected = (expectedSource ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        if (adminSshUnpinned)
        {
            return true;
        }

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
               || actual is "0.0.0.0/0" or "::/0";
    }
}
