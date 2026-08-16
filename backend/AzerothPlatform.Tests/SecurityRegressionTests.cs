using System.Net;
using AzerothPlatform.ClientContent;
using AzerothPlatform.Core.Contracts;
using Xunit;

namespace AzerothPlatform.Tests;

/// <summary>
/// Regression tests for the security remediation work: deny-by-default authorization, public-route
/// exceptions, and client-manifest signature verification. These lock in the hardened behaviour so a
/// future change that reopens a hole fails CI.
/// </summary>
public sealed class SecurityRegressionTests : IClassFixture<AzerothPlatformWebApplicationFactory>
{
    private readonly AzerothPlatformWebApplicationFactory _factory;

    public SecurityRegressionTests(AzerothPlatformWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ------------------------------------------------------------------
    // Deny-by-default authorization
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProtectedRoute_WithoutAuthentication_Returns401()
    {
        var client = _factory.CreateClient();
        // X-Anon makes the test auth handler treat the request as an unauthenticated caller.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/stacks");
        request.Headers.Add(TestAuthHandler.AnonymousHeader, "1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthRoute_WithoutAuthentication_Returns200()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add(TestAuthHandler.AnonymousHeader, "1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LauncherPreviewAsset_WithoutAuthentication_IsNotUnauthorized()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/launcher/templates/wotlk/logo");
        request.Headers.Add(TestAuthHandler.AnonymousHeader, "1");

        var response = await client.SendAsync(request);

        // Admin launcher preview assets are [AllowAnonymous]; they must not be gated by auth.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Client-manifest signing (supply-chain integrity)
    // ------------------------------------------------------------------

    [Fact]
    public void ManifestSignature_RoundTrips_ForUntamperedManifest()
    {
        var (privateKey, publicKey) = ManifestSigner.GenerateKeyPair();
        var manifest = BuildManifest();

        ManifestSigner.Sign(manifest, privateKey);

        Assert.False(string.IsNullOrEmpty(manifest.Signature));
        Assert.True(ManifestSigner.Verify(manifest, publicKey));
    }

    [Fact]
    public void ManifestSignature_Fails_WhenAFileHashIsTampered()
    {
        var (privateKey, publicKey) = ManifestSigner.GenerateKeyPair();
        var manifest = BuildManifest();
        ManifestSigner.Sign(manifest, privateKey);

        // Simulate a MITM swapping a file's hash after signing.
        manifest.Files[0].Sha256 = new string('f', 64);

        Assert.False(ManifestSigner.Verify(manifest, publicKey));
    }

    [Fact]
    public void ManifestSignature_Fails_WithADifferentPublicKey()
    {
        var (privateKey, _) = ManifestSigner.GenerateKeyPair();
        var (_, otherPublicKey) = ManifestSigner.GenerateKeyPair();
        var manifest = BuildManifest();
        ManifestSigner.Sign(manifest, privateKey);

        Assert.False(ManifestSigner.Verify(manifest, otherPublicKey));
    }

    [Fact]
    public void ManifestSignature_Fails_WhenUnsigned()
    {
        var (_, publicKey) = ManifestSigner.GenerateKeyPair();
        var manifest = BuildManifest();

        Assert.False(ManifestSigner.Verify(manifest, publicKey));
    }

    private static ClientManifest BuildManifest() => new()
    {
        Version = "v1",
        VerifyToken = "token",
        GeneratedAt = DateTime.UtcNow,
        TotalSize = 3,
        Files =
        {
            new ManifestFile { RelativePath = "Data/patch-B.mpq", Size = 1, Sha256 = new string('a', 64), Group = ManifestFileGroup.Managed },
            new ManifestFile { RelativePath = "Wow.exe", Size = 2, Sha256 = new string('b', 64), Group = ManifestFileGroup.Base },
        }
    };
}
