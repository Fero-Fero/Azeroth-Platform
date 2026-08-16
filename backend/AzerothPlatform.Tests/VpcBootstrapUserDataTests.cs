using AzerothPlatform.Core.Contracts;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class VpcBootstrapUserDataTests
{
    [Fact]
    public void SanitizeSshUser_EmptyOrInvalid_BecomesOperator()
    {
        Assert.Equal("azp-admin", VpcBootstrapUserData.SanitizeSshUser(""));
        Assert.Equal("azp-admin", VpcBootstrapUserData.SanitizeSshUser("Root"));
        Assert.Equal("azp-admin", VpcBootstrapUserData.SanitizeSshUser("not valid"));
        Assert.Equal("azp-admin", VpcBootstrapUserData.SanitizeSshUser("azp-admin"));
        Assert.Equal("ubuntu", VpcBootstrapUserData.SanitizeSshUser("ubuntu"));
    }

    [Fact]
    public void EnsureLaunchSshUser_RejectsRoot()
    {
        var ex = Assert.Throws<ArgumentException>(() => VpcBootstrapUserData.EnsureLaunchSshUser("root"));
        Assert.Contains("azp-admin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLaunchScript_CreatesOperatorUserAndDoesNotLockUbuntu()
    {
        var script = VpcBootstrapUserData.BuildLaunchScript("azp-admin", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIoperator test");

        Assert.Contains("useradd --create-home --shell /bin/bash \"azp-admin\"", script, StringComparison.Ordinal);
        Assert.Contains("usermod -aG docker azp-admin", script, StringComparison.Ordinal);
        Assert.Contains("azp-admin ALL=(ALL) NOPASSWD:ALL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PermitRootLogin no", script, StringComparison.Ordinal);
        Assert.DoesNotContain("truncate -s 0", script, StringComparison.Ordinal);
        Assert.Contains("apt-get install -y docker.io ufw unattended-upgrades", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.io docker-compose-v2", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSshHardeningDropIn_AwsKeepsUbuntuForInstanceConnect()
    {
        var dropIn = VpcBootstrapUserData.BuildSshHardeningDropIn("azp-admin", enableAwsInstanceConnect: true);

        Assert.Contains("PermitRootLogin no", dropIn, StringComparison.Ordinal);
        Assert.Contains("PasswordAuthentication no", dropIn, StringComparison.Ordinal);
        Assert.Contains("AllowUsers azp-admin ubuntu", dropIn, StringComparison.Ordinal);
        Assert.Contains("Match User ubuntu", dropIn, StringComparison.Ordinal);
        Assert.Contains("eic_run_authorized_keys", dropIn, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSshHardeningDropIn_NonAwsAllowsOperatorOnly()
    {
        var dropIn = VpcBootstrapUserData.BuildSshHardeningDropIn("azp-admin", enableAwsInstanceConnect: false);

        Assert.Contains("AllowUsers azp-admin", dropIn, StringComparison.Ordinal);
        Assert.DoesNotContain("ubuntu", dropIn, StringComparison.Ordinal);
        Assert.DoesNotContain("Match User", dropIn, StringComparison.Ordinal);
    }
}
