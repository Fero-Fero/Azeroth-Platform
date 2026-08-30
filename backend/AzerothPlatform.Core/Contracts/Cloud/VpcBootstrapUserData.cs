using System.Text;
using System.Text.RegularExpressions;

namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// EC2 / cloud-init user-data script that prepares a fresh Ubuntu instance for Azeroth Platform
/// without manual SSH. Paste into the "User data" field when launching the instance.
/// </summary>
public static class VpcBootstrapUserData
{
    public const string DefaultOperatorUser = "azp-admin";

    /// <summary>
    /// Image-default SSH user for first login (the bootstrap key). Filenames use this so
    /// <c>ubuntu.pem</c> / <c>root.pem</c> match the account Verify VPC SSHs as.
    /// </summary>
    public static string ImageDefaultSshUser(CloudProvider provider)
        => provider is CloudProvider.DigitalOcean or CloudProvider.Hetzner or CloudProvider.Vultr
            ? "root"
            : "ubuntu";

    private static readonly Regex SafeLinuxUser = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ForbiddenUsers = new(StringComparer.Ordinal)
    {
        "root", "nobody", "daemon", "bin", "sys", "sync", "sshd", "www-data", "messagebus",
    };

    private static readonly HashSet<string> ImageDefaultUsers = new(StringComparer.Ordinal)
    {
        "ubuntu", "debian", "azureuser", "ec2-user", "admin", "centos", "fedora",
    };

    public static bool IsForbiddenSshUser(string sshUser)
    {
        var user = (sshUser ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(user)
               || ForbiddenUsers.Contains(user)
               || user.StartsWith("systemd-", StringComparison.Ordinal);
    }

    public static bool IsImageDefaultSshUser(string sshUser)
        => ImageDefaultUsers.Contains((sshUser ?? string.Empty).Trim().ToLowerInvariant());

    /// <summary>Valid Linux name, not root/system. Empty or invalid names become <see cref="DefaultOperatorUser"/>.</summary>
    public static string SanitizeSshUser(string sshUser)
    {
        var trimmed = (sshUser ?? string.Empty).Trim().ToLowerInvariant();
        if (!SafeLinuxUser.IsMatch(trimmed) || IsForbiddenSshUser(trimmed))
        {
            return DefaultOperatorUser;
        }

        return trimmed;
    }

    /// <summary>Launch path: reject root/system users instead of silently remapping them.</summary>
    public static string EnsureLaunchSshUser(string sshUser)
    {
        var trimmed = (sshUser ?? string.Empty).Trim().ToLowerInvariant();
        if (IsForbiddenSshUser(trimmed) && SafeLinuxUser.IsMatch(trimmed))
        {
            throw new ArgumentException(
                $"SSH user '{trimmed}' is not allowed. Use a dedicated operator user such as {DefaultOperatorUser}.");
        }

        return SanitizeSshUser(trimmed);
    }

    public static string BuildLaunchScript(string sshUser = DefaultOperatorUser, string? authorizedPublicKey = null)
    {
        var user = SanitizeSshUser(sshUser);
        var keyB64 = EncodeAuthorizedKey(authorizedPublicKey);
        // A Windows checkout stores this source with CRLF endings, which the raw string literal carries
        // verbatim, and bash reads the trailing CR as part of the last word on each line.
        return NormalizeLineEndings($$"""
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

            echo "==> Ensuring operator user {{user}} exists..."
            if ! id "{{user}}" >/dev/null 2>&1; then
              $SUDO useradd --create-home --shell /bin/bash "{{user}}"
            fi
            $SUDO usermod -aG sudo "{{user}}" 2>/dev/null || $SUDO usermod -aG wheel "{{user}}" 2>/dev/null || true
            $SUDO mkdir -p "/home/{{user}}/.ssh"
            $SUDO chmod 700 "/home/{{user}}/.ssh"
            AUTH_TMP="$(mktemp)"
            if [ -n "{{keyB64}}" ]; then
              echo "{{keyB64}}" | $SUDO base64 -d >> "$AUTH_TMP" || true
            fi
            if [ -s "$AUTH_TMP" ]; then
              $SUDO sort -u "$AUTH_TMP" | $SUDO tee "/home/{{user}}/.ssh/authorized_keys" >/dev/null
              $SUDO chmod 600 "/home/{{user}}/.ssh/authorized_keys"
            fi
            rm -f "$AUTH_TMP"
            $SUDO chown -R "{{user}}:{{user}}" "/home/{{user}}/.ssh"

            echo "==> Configuring non-interactive sudo (NOPASSWD, disable use_pty)..."
            $SUDO tee /etc/sudoers.d/99-azeroth-platform >/dev/null <<EOF
            Defaults !use_pty
            Defaults:{{user}} !use_pty
            Defaults !requiretty
            Defaults:{{user}} !requiretty
            {{user}} ALL=(ALL) NOPASSWD:ALL
            EOF
            $SUDO chmod 440 /etc/sudoers.d/99-azeroth-platform
            $SUDO /usr/sbin/visudo -c -f /etc/sudoers.d/99-azeroth-platform || echo "WARN: visudo check failed; continuing"

            echo "==> Updating package lists..."
            $SUDO apt-get update -qq || true
            echo "==> Installing Docker, ufw, and unattended-upgrades..."
            if ! $SUDO apt-get install -y docker.io ufw unattended-upgrades; then
              $SUDO apt-get install -y software-properties-common || true
              $SUDO add-apt-repository -y universe || true
              $SUDO apt-get update -qq
              $SUDO apt-get install -y docker.io ufw unattended-upgrades
            fi
            $SUDO apt-get install -y docker-compose-v2 || true
            echo "==> Starting Docker..."
            $SUDO systemctl enable --now docker
            echo "==> Enabling automatic security updates..."
            $SUDO tee /etc/apt/apt.conf.d/20auto-upgrades >/dev/null <<'EOF'
            APT::Periodic::Update-Package-Lists "1";
            APT::Periodic::Unattended-Upgrade "1";
            EOF
            $SUDO systemctl enable --now apt-daily.timer apt-daily-upgrade.timer || true
            $SUDO systemctl enable unattended-upgrades || true
            echo "==> Granting Docker access to {{user}}..."
            $SUDO usermod -aG docker {{user}}

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
            """);
    }

    private static string NormalizeLineEndings(string script) => script.Replace("\r\n", "\n");

    /// <summary>
    /// sshd drop-in applied by Finalize SSH hardening. AWS keeps <c>ubuntu</c> for EC2 Instance Connect only.
    /// </summary>
    public static string BuildSshHardeningDropIn(string operatorUser, bool enableAwsInstanceConnect)
    {
        var user = SanitizeSshUser(operatorUser);
        var allow = enableAwsInstanceConnect ? $"{user} ubuntu" : user;
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Azeroth Platform - do not edit");
        builder.AppendLine("PermitRootLogin no");
        builder.AppendLine("PasswordAuthentication no");
        builder.AppendLine("KbdInteractiveAuthentication no");
        builder.AppendLine($"AllowUsers {allow}");
        if (enableAwsInstanceConnect)
        {
            builder.AppendLine();
            builder.AppendLine("Match User ubuntu");
            builder.AppendLine("    AuthorizedKeysCommand /usr/share/ec2-instance-connect/eic_run_authorized_keys %u %f");
            builder.AppendLine("    AuthorizedKeysCommandUser ec2-instance-connect");
        }

        return builder.ToString();
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

    public static VpcLaunchUserDataDto CreateDto(string sshUser = DefaultOperatorUser)
        => new()
        {
            SshUser = SanitizeSshUser(sshUser),
            Script = BuildLaunchScript(sshUser),
            RemoteOs = RemoteHostOs.Linux,
            ScriptKind = "bash",
            Instructions =
                "Paste this script into your cloud provider's startup/user-data field when creating a new " +
                "Ubuntu VM (AWS User data, GCP Startup script, DigitalOcean User data, etc.). It creates " +
                $"operator user {SanitizeSshUser(sshUser)} (not root). If the server already exists, skip this " +
                "and use Connect server instead."
        };

    private static string EncodeAuthorizedKey(string? authorizedPublicKey)
    {
        var key = (authorizedPublicKey ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(key) || key.Contains('\n') || key.Contains('\r'))
        {
            return string.Empty;
        }

        if (!key.StartsWith("ssh-", StringComparison.Ordinal) && !key.StartsWith("ecdsa-", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(key + "\n"));
    }
}

public sealed class VpcLaunchUserDataDto
{
    public string SshUser { get; set; } = VpcBootstrapUserData.DefaultOperatorUser;
    public string Script { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public RemoteHostOs RemoteOs { get; set; } = RemoteHostOs.Linux;
    public string ScriptKind { get; set; } = "bash";
}
