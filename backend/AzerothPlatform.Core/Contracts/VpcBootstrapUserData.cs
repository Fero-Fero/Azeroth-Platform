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

            if [ "$(id -u)" -eq 0 ]; then
              SUDO=""
            elif command -v sudo >/dev/null 2>&1; then
              SUDO="sudo"
            else
              echo "Run as root or install sudo, then re-run this script." >&2
              exit 1
            fi

            echo "==> Updating package lists..."
            $SUDO apt-get update -qq
            echo "==> Installing Docker, Compose, ufw, and unattended-upgrades..."
            $SUDO apt-get install -y docker.io docker-compose-v2 ufw unattended-upgrades
            echo "==> Starting Docker..."
            $SUDO systemctl enable --now docker
            echo "==> Enabling automatic security updates..."
            $SUDO systemctl enable unattended-upgrades || true
            echo "==> Granting Docker access to {{user}}..."
            $SUDO usermod -aG docker {{user}}

            echo "==> Configuring non-interactive sudo (NOPASSWD, disable use_pty)..."
            $SUDO tee /etc/sudoers.d/99-azeroth-platform >/dev/null <<EOF
            Defaults !use_pty
            Defaults:{{user}} !use_pty
            Defaults !requiretty
            Defaults:{{user}} !requiretty
            {{user}} ALL=(ALL) NOPASSWD:ALL
            EOF
            $SUDO chmod 440 /etc/sudoers.d/99-azeroth-platform
            $SUDO /usr/sbin/visudo -c -f /etc/sudoers.d/99-azeroth-platform

            echo "==> Configuring host firewall (ufw)..."
            $SUDO ufw --force reset || true
            $SUDO ufw default deny incoming
            $SUDO ufw default allow outgoing
            $SUDO ufw allow 22/tcp comment 'SSH'
            $SUDO ufw allow 3724/tcp comment 'Authserver'
            $SUDO ufw allow 8085/tcp comment 'Worldserver'
            $SUDO ufw allow {{StackNetworkDefaults.DefaultArmoryPort}}/tcp comment 'Armory'
            $SUDO ufw allow {{StackNetworkDefaults.DefaultClientPort}}/tcp comment 'Client files'
            $SUDO ufw --force enable

            $SUDO mkdir -p /var/lib/azeroth-platform
            date -u +%Y-%m-%dT%H:%M:%SZ | $SUDO tee /var/lib/azeroth-platform/bootstrap-ready >/dev/null
            echo "==> Bootstrap complete. Docker, ufw, and OS baselines are installed."
            $SUDO docker --version 2>/dev/null || true
            $SUDO ufw status verbose 2>/dev/null || true
            """;
    }

    /// <summary>
    /// Ubuntu 24.04 sudo enables <c>Defaults use_pty</c>, which makes <c>sudo -n</c> fail over SSH
    /// with "a password is required" even when NOPASSWD is set. First Time Setup and launch user-data
    /// must write the same file.
    /// </summary>
    public static string BuildPasswordlessSudoers(string sshUser)
    {
        var user = SanitizeSshUser(sshUser);
        return
            $"Defaults !use_pty\nDefaults:{user} !use_pty\nDefaults !requiretty\nDefaults:{user} !requiretty\n{user} ALL=(ALL) NOPASSWD:ALL\n";
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
