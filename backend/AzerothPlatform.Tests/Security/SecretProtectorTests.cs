using System.Security.Cryptography;
using AzerothPlatform.Infrastructure.Services;
using AzerothPlatform.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzerothPlatform.Tests.Security;

public sealed class SecretProtectorTests : IDisposable
{
    private readonly TempDir _keyDir = new("azp-secret");

    public void Dispose() => _keyDir.Dispose();

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

    private SecretProtector CreateProtector()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:KeyDir"] = _keyDir.Path,
            })
            .Build();
        return new SecretProtector(config, NullLogger<SecretProtector>.Instance);
    }
}
