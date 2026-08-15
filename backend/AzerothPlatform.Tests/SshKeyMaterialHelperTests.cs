using System.Text;
using AzerothPlatform.Infrastructure.Services.Cloud;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class SshKeyMaterialHelperTests
{
    [Fact]
    public void GenerateKeyPair_PublicKey_IsAsciiOpenSshRsa()
    {
        var pair = SshKeyMaterialHelper.GenerateKeyPair();

        Assert.StartsWith("ssh-rsa ", pair.OpenSshPublicKey, StringComparison.Ordinal);
        Assert.Equal(2, pair.OpenSshPublicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.True(pair.OpenSshPublicKey.All(ch => ch <= 127));
        Assert.Contains("BEGIN PRIVATE KEY", pair.PrivateKeyPem, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractOpenSshPublicKey_RoundTripsGeneratedPrivateKey()
    {
        var pair = SshKeyMaterialHelper.GenerateKeyPair();
        var extracted = SshKeyMaterialHelper.ExtractOpenSshPublicKey(pair.PrivateKeyPem);

        Assert.Equal(pair.OpenSshPublicKey, extracted);
    }

    [Fact]
    public void ToAwsImportPublicKeyMaterial_Base64EncodesUtf8OpenSshLine()
    {
        var pair = SshKeyMaterialHelper.GenerateKeyPair();
        var encoded = SshKeyMaterialHelper.ToAwsImportPublicKeyMaterial(pair.OpenSshPublicKey);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

        Assert.Equal(pair.OpenSshPublicKey, decoded);
        Assert.True(encoded.All(ch => ch <= 127));
    }

    [Fact]
    public void ToAwsImportPublicKeyMaterial_StripsNonAsciiComment()
    {
        var encoded = SshKeyMaterialHelper.ToAwsImportPublicKeyMaterial(
            "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC3 user@host-åäö");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

        Assert.Equal("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC3", decoded);
    }
}
