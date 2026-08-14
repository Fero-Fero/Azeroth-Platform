using System.Text.RegularExpressions;

namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// EC2 / cloud-init user-data script that prepares a fresh Ubuntu instance for Azeroth Platform
/// without manual SSH. Paste into the "User data" field when launching the instance.
/// </summary>
public static class VpcBootstrapUserData
{
    private static readonly Regex SafeLinuxUser = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);

    public static string BuildLaunchScript(string sshUser = "ubuntu")
    {
        var user = SanitizeSshUser(sshUser);
        return $$"""
            #!/bin/bash
            set -euxo pipefail
            export DEBIAN_FRONTEND=noninteractive

            if ! command -v apt-get >/dev/null 2>&1; then
              echo "This bootstrap script supports Ubuntu/Debian only." >&2
              exit 1
            fi

            apt-get update -qq
            apt-get install -y docker.io docker-compose-v2
            systemctl enable --now docker
            usermod -aG docker {{user}}

            echo '{{user}} ALL=(ALL) NOPASSWD:ALL' >/etc/sudoers.d/90-azeroth-platform
            chmod 440 /etc/sudoers.d/90-azeroth-platform

            mkdir -p /var/lib/azeroth-platform
            date -u +%Y-%m-%dT%H:%M:%SZ > /var/lib/azeroth-platform/bootstrap-ready
            """;
    }

    public static VpcLaunchUserDataDto CreateDto(string sshUser = "ubuntu")
        => new()
        {
            SshUser = SanitizeSshUser(sshUser),
            Script = BuildLaunchScript(sshUser),
            Instructions =
                "Paste this script into your cloud provider's startup/user-data field when creating a new " +
                "Ubuntu VM (AWS User data, GCP Startup script, DigitalOcean User data, etc.). It runs once on " +
                "first boot. If the server already exists, skip this and use Connect server instead."
        };

    private static string SanitizeSshUser(string sshUser)
    {
        var trimmed = (sshUser ?? string.Empty).Trim().ToLowerInvariant();
        if (!SafeLinuxUser.IsMatch(trimmed))
        {
            return "ubuntu";
        }

        return trimmed;
    }
}

public sealed class VpcLaunchUserDataDto
{
    public string SshUser { get; set; } = "ubuntu";
    public string Script { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
}
