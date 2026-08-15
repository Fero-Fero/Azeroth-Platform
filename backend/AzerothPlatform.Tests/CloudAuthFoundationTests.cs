using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services;
using AzerothPlatform.Infrastructure.Services.Cloud;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class CloudAuthFoundationTests
{
    [Fact]
    public void Pkce_VerifierAndChallenge_AreUrlSafeAndStable()
    {
        var verifier = CloudOAuthPkce.CreateCodeVerifier();
        var challenge = CloudOAuthPkce.CreateS256Challenge(verifier);
        var again = CloudOAuthPkce.CreateS256Challenge(verifier);

        Assert.False(string.IsNullOrWhiteSpace(verifier));
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);
        Assert.Equal(again, challenge);
        Assert.NotEqual(verifier, CloudOAuthPkce.CreateCodeVerifier());
    }

    [Fact]
    public async Task OAuthStateStore_Take_IsSingleUse()
    {
        var store = new MemoryCloudOAuthStateStore(new MemoryCache(new MemoryCacheOptions()));
        var created = await store.CreateAsync(
            CloudProvider.DigitalOcean,
            codeVerifier: "verifier",
            returnUrl: "/admin/cloud",
            reconnectConnectionId: null,
            label: "DO team",
            CancellationToken.None);

        var first = await store.TakeAsync(created.State, CancellationToken.None);
        var second = await store.TakeAsync(created.State, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(CloudProvider.DigitalOcean, first.Provider);
        Assert.Equal("verifier", first.CodeVerifier);
        Assert.Null(second);
        Assert.Null(await store.TakeAsync("missing", CancellationToken.None));
    }

    [Fact]
    public void OAuthEnvelope_ParsesTypedJson_AndIgnoresServiceAccountJson()
    {
        var envelopeJson =
            """{"type":"oauth_user","accessToken":"do-token","refreshToken":"refresh","scope":"read write"}""";
        Assert.True(CloudProviderCredentialStore.TryParseOAuthEnvelope(envelopeJson, out var envelope));
        Assert.Equal("do-token", envelope.AccessToken);
        Assert.Equal("refresh", envelope.RefreshToken);

        var serviceAccount = """{"type":"service_account","project_id":"demo","private_key":"x"}""";
        Assert.False(CloudProviderCredentialStore.TryParseOAuthEnvelope(serviceAccount, out _));
        Assert.False(CloudProviderCredentialStore.TryParseOAuthEnvelope("plain-api-token", out _));
    }

    [Fact]
    public void AwsIamConnectTemplate_EmbedsAccountIdExternalIdAndActions()
    {
        const string accountId = "123456789012";
        const string externalId = "ext-id-aaaa-bbbb";
        var templates = AwsIamConnectTemplate.BuildAll(accountId, externalId);

        Assert.Equal(3, templates.Count);
        Assert.Equal(new[] { "ReadOnly", "Standard", "Full" }, templates.Select(item => item.PolicyTier));

        var full = templates.Single(item => item.PolicyTier == "Full");
        Assert.Contains(accountId, full.CloudFormationYaml, StringComparison.Ordinal);
        Assert.Contains(externalId, full.CloudFormationYaml, StringComparison.Ordinal);
        Assert.Contains("arn:aws:iam::${PlatformAccountId}:root", full.CloudFormationYaml, StringComparison.Ordinal);
        Assert.Contains("ec2:DescribeRegions", full.CloudFormationYaml, StringComparison.Ordinal);
        Assert.Contains("ec2:DescribeInstanceTypes", full.CloudFormationYaml, StringComparison.Ordinal);
        Assert.Contains("ec2:RunInstances", full.CloudFormationYaml, StringComparison.Ordinal);
        Assert.Contains("ssm:SendCommand", full.CloudFormationYaml, StringComparison.Ordinal);
        Assert.Contains("ec2:AuthorizeSecurityGroupIngress", full.CloudFormationYaml, StringComparison.Ordinal);

        var readOnly = templates.Single(item => item.PolicyTier == "ReadOnly");
        Assert.DoesNotContain("ec2:RunInstances", readOnly.CloudFormationYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ssm:SendCommand", readOnly.CloudFormationYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AwsAssumedRoleEnvelope_ParsesTypedJson_AndRejectsAccessKeys()
    {
        var envelopeJson =
            """{"type":"assumed_role","roleArn":"arn:aws:iam::111122223333:role/AzerothPlatformAccess","externalId":"ext-1"}""";
        Assert.True(CloudProviderCredentialStore.TryParseAwsAssumedRole(envelopeJson, out var envelope));
        Assert.Equal("arn:aws:iam::111122223333:role/AzerothPlatformAccess", envelope.RoleArn);
        Assert.Equal("ext-1", envelope.ExternalId);

        Assert.False(CloudProviderCredentialStore.TryParseAwsAssumedRole(
            """{"AccessKeyId":"AKIATEST","SecretAccessKey":"secret"}""",
            out _));
        Assert.False(CloudProviderCredentialStore.TryParseOAuthEnvelope(envelopeJson, out _));
    }

    [Fact]
    public void UnprotectAwsCredentials_RejectsAssumedRoleBlob()
    {
        var protector = new PassthroughSecretProtector();
        var protectedRole = CloudProviderCredentialStore.ProtectAwsAssumedRole(
            protector,
            new CloudProviderCredentialStore.AwsAssumedRoleEnvelope
            {
                RoleArn = "arn:aws:iam::111122223333:role/AzerothPlatformAccess",
                ExternalId = "ext-1",
            });

        var ex = Assert.Throws<InvalidOperationException>(
            () => CloudProviderCredentialStore.UnprotectAwsCredentials(protector, protectedRole));
        Assert.Contains("IAM role", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AwsCredentialResolver_ManualKeys_ReturnWithoutSessionToken()
    {
        var protector = new PassthroughSecretProtector();
        var protectedKeys = CloudProviderCredentialStore.ProtectAwsCredentials(
            protector,
            new CloudProviderCredentialStore.AwsCredentials("AKIATEST", "secret"));
        var entity = new CloudProviderConnectionEntity
        {
            Id = "aws-manual-1",
            Provider = CloudProvider.Aws.ToString(),
            ProtectedCredentials = protectedKeys,
        };

        var resolver = new AwsCredentialResolver(
            protector,
            new AwsStsClient(Options.Create(new CloudOAuthOptions())),
            new MemoryCache(new MemoryCacheOptions()));

        var credentials = await resolver.ResolveAsync(entity);

        Assert.Equal("AKIATEST", credentials.AccessKeyId);
        Assert.Equal("secret", credentials.SecretAccessKey);
        Assert.True(string.IsNullOrWhiteSpace(credentials.SessionToken));
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string? plaintext) => plaintext ?? string.Empty;

        public string Unprotect(string? protectedValue) => protectedValue ?? string.Empty;

        public bool IsProtected(string? value) => false;
    }
}

public sealed class CloudAuthApiTests : IClassFixture<AzerothPlatformWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AzerothPlatformWebApplicationFactory _factory;

    public CloudAuthApiTests(AzerothPlatformWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ListAuthProviders_ReturnsAllSixProviders()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/cloud/auth/providers");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        foreach (var provider in new[] { "DigitalOcean", "Aws", "Gcp", "Azure", "Hetzner", "Vultr" })
        {
            Assert.Contains(provider, body, StringComparison.Ordinal);
        }

        var providers = JsonSerializer.Deserialize<List<CloudAuthProviderStatusDto>>(body, JsonOptions);
        var aws = Assert.Single(providers!, item => item.Provider == CloudProvider.Aws);
        Assert.Equal(CloudLoginMode.AssumedRole, aws.LoginMode);
        Assert.True(aws.IsImplemented);
        Assert.False(aws.IsConfigured);
        Assert.Equal("Connect AWS account", aws.SignInLabel);
    }

    [Fact]
    public async Task StartAwsAssumeRole_WhenPlatformAccountIdMissing_Returns400UnlessStsResolves()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cloud/auth/Aws/start", new { });
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadFromJsonAsync<CloudAuthStartResultDto>(JsonOptions);
            Assert.NotNull(result?.AwsTemplates);
            Assert.NotEmpty(result!.AwsTemplates!);
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PlatformAccountId", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAwsAssumeRole_WhenConfigured_ReturnsCloudFormationTemplates()
    {
        var client = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CloudOAuth:Aws:PlatformAccountId", "123456789012"))
            .CreateClient();

        var response = await client.PostAsJsonAsync("/api/cloud/auth/Aws/start", new { label = "AWS lab" });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudAuthStartResultDto>(JsonOptions);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.ExternalId));
        Assert.Contains("cloudformation", result.CloudFormationConsoleUrl, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.AwsTemplates);
        Assert.Equal(3, result.AwsTemplates!.Count);
        Assert.Contains("123456789012", result.AwsTemplates[0].CloudFormationYaml, StringComparison.Ordinal);
        Assert.Contains(result.ExternalId, result.AwsTemplates[0].CloudFormationYaml, StringComparison.Ordinal);
        Assert.Null(result.AuthorizationUrl);
    }

    [Fact]
    public async Task CompleteAwsAssumeRole_WhenRoleArnMissing_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cloud/auth/Aws/complete", new { externalId = "ext-1" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Role ARN", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartDigitalOceanOAuth_WhenNotImplemented_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cloud/auth/DigitalOcean/start", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OAuthCallback_WithoutAuthentication_IsNotUnauthorized()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/cloud/auth/DigitalOcean/callback?code=demo&state=missing");
        request.Headers.Add(TestAuthHandler.AnonymousHeader, "1");

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task SetupDialog_UnknownConnection_Returns404()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/cloud/connections/does-not-exist/setup-dialog");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
