using System.Security.Cryptography;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class SecretProtectorTests
{
    [Fact]
    public void Protect_then_Unprotect_round_trips()
    {
        var protector = CreateProtector();
        var token = protector.Protect("ssh-ed25519 AAAA");

        token.Should().StartWith("enc:v1:");
        protector.Unprotect(token).Should().Be("ssh-ed25519 AAAA");
    }

    [Fact]
    public void Unprotect_rejects_untagged_plaintext()
    {
        var protector = CreateProtector();

        var act = () => protector.Unprotect("-----BEGIN OPENSSH PRIVATE KEY-----");

        act.Should().Throw<CryptographicException>().WithMessage("*encryption marker*");
    }

    private static SecretProtector CreateProtector()
    {
        var keyDir = Path.Combine(Path.GetTempPath(), "azp-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:KeyDir"] = keyDir,
            })
            .Build();
        return new SecretProtector(config, NullLogger<SecretProtector>.Instance);
    }
}
