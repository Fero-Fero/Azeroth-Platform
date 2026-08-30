using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services;
using AzerothPlatform.Infrastructure.Services.Cloud;
using AzerothPlatform.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzerothPlatform.Tests.Cloud;

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
        Assert.Contains("ec2:TerminateInstances", full.CloudFormationYaml, StringComparison.Ordinal);
        Assert.Contains("ec2:AuthorizeSecurityGroupIngress", full.CloudFormationYaml, StringComparison.Ordinal);

        var readOnly = templates.Single(item => item.PolicyTier == "ReadOnly");
        Assert.DoesNotContain("ec2:RunInstances", readOnly.CloudFormationYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ec2:TerminateInstances", readOnly.CloudFormationYaml, StringComparison.Ordinal);
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

public sealed class DigitalOceanFirewallRuleTests
{
    [Fact]
    public void InboundRuleCovers_ExactPortAndCidr()
    {
        var rule = new DigitalOceanClient.DigitalOceanFirewallInboundRule
        {
            Protocol = "tcp",
            Ports = "22",
            SourceAddresses = ["203.0.113.10/32"],
        };

        Assert.True(DigitalOceanClient.InboundRuleCovers(rule, 22, "203.0.113.10/32"));
        Assert.False(DigitalOceanClient.InboundRuleCovers(rule, 22, "0.0.0.0/0"));
        Assert.False(DigitalOceanClient.InboundRuleCovers(rule, 3724, "203.0.113.10/32"));
        Assert.False(DigitalOceanClient.InboundRuleOpensPortPublicly(rule, 22));
    }

    [Fact]
    public void InboundRuleCovers_PublicCidrAndPortRange()
    {
        var rule = new DigitalOceanClient.DigitalOceanFirewallInboundRule
        {
            Protocol = "tcp",
            Ports = "8085-8101",
            SourceAddresses = ["0.0.0.0/0"],
        };

        Assert.True(DigitalOceanClient.InboundRuleCovers(rule, 8085, "0.0.0.0/0"));
        Assert.True(DigitalOceanClient.InboundRuleCovers(rule, 8100, "203.0.113.10/32"));
        Assert.True(DigitalOceanClient.InboundRuleOpensPortPublicly(rule, 8101));
        Assert.False(DigitalOceanClient.InboundRuleOpensPortPublicly(rule, 3306));
    }
}

public sealed class VultrFirewallRuleTests
{
    [Fact]
    public void SplitCidr_ParsesPrefix()
    {
        var (subnet, size) = VultrClient.SplitCidr("203.0.113.10/32");
        Assert.Equal("203.0.113.10", subnet);
        Assert.Equal(32, size);

        var open = VultrClient.SplitCidr("0.0.0.0/0");
        Assert.Equal("0.0.0.0", open.Subnet);
        Assert.Equal(0, open.SubnetSize);
    }

    [Fact]
    public void FirewallRuleCovers_ExactPortAndCidr()
    {
        var rule = new VultrClient.VultrFirewallInboundRule
        {
            Protocol = "tcp",
            Port = "22",
            Subnet = "203.0.113.10",
            SubnetSize = 32,
        };

        Assert.True(VultrClient.FirewallRuleCovers(rule, 22, "203.0.113.10/32"));
        Assert.False(VultrClient.FirewallRuleCovers(rule, 22, "0.0.0.0/0"));
        Assert.False(VultrClient.FirewallRuleCovers(rule, 3724, "203.0.113.10/32"));
        Assert.False(VultrClient.FirewallRuleOpensPortPublicly(rule, 22));
    }

    [Fact]
    public void FirewallRuleCovers_PublicCidr()
    {
        var rule = new VultrClient.VultrFirewallInboundRule
        {
            Protocol = "tcp",
            Port = "3724",
            Subnet = "0.0.0.0",
            SubnetSize = 0,
        };

        Assert.True(VultrClient.FirewallRuleCovers(rule, 3724, "0.0.0.0/0"));
        Assert.True(VultrClient.FirewallRuleCovers(rule, 3724, "203.0.113.10/32"));
        Assert.True(VultrClient.FirewallRuleOpensPortPublicly(rule, 3724));
        Assert.False(VultrClient.FirewallRuleOpensPortPublicly(rule, 3306));
    }
}

public sealed class GcpFirewallRuleTests
{
    [Fact]
    public void FirewallRuleCovers_ExactPortAndCidr()
    {
        var rule = new GcpComputeClient.GcpFirewallProbeRule
        {
            Protocol = "tcp",
            Ports = ["22"],
            SourceRanges = ["203.0.113.10/32"],
            TargetTags = [GcpComputeClient.PlatformNetworkTag],
        };

        Assert.True(GcpComputeClient.FirewallRuleCovers(rule, 22, "203.0.113.10/32"));
        Assert.False(GcpComputeClient.FirewallRuleCovers(rule, 22, "0.0.0.0/0"));
        Assert.False(GcpComputeClient.FirewallRuleCovers(rule, 3724, "203.0.113.10/32"));
        Assert.False(GcpComputeClient.FirewallRuleOpensPortPublicly(rule, 22));
    }

    [Fact]
    public void FirewallRuleCovers_PublicCidrAndPortRange()
    {
        var rule = new GcpComputeClient.GcpFirewallProbeRule
        {
            Protocol = "tcp",
            Ports = ["8085-8101"],
            SourceRanges = ["0.0.0.0/0"],
            TargetTags = [GcpComputeClient.PlatformNetworkTag],
        };

        Assert.True(GcpComputeClient.FirewallRuleCovers(rule, 8085, "0.0.0.0/0"));
        Assert.True(GcpComputeClient.FirewallRuleCovers(rule, 8100, "203.0.113.10/32"));
        Assert.True(GcpComputeClient.FirewallRuleOpensPortPublicly(rule, 8101));
        Assert.False(GcpComputeClient.FirewallRuleOpensPortPublicly(rule, 3306));
    }

    [Fact]
    public void BuildFirewallName_StartsWithLetterAndIncludesPort()
    {
        var name = GcpComputeClient.BuildFirewallName("abc123def456", 3724);
        Assert.StartsWith("azp-", name, StringComparison.Ordinal);
        Assert.Contains("p3724", name, StringComparison.Ordinal);
        Assert.True(name.Length <= 63);
        Assert.True(char.IsLetter(name[0]));
    }
}

public sealed class AzureNsgRuleTests
{
    [Fact]
    public void NsgRuleCovers_ExactPortAndCidr()
    {
        var rule = new AzureComputeClient.AzureNsgProbeRule
        {
            Name = "azp-tcp-22",
            Protocol = "Tcp",
            DestinationPortRange = "22",
            SourceAddressPrefix = "203.0.113.10/32",
            Access = "Allow",
        };

        Assert.True(AzureComputeClient.NsgRuleCovers(rule, 22, "203.0.113.10/32"));
        Assert.False(AzureComputeClient.NsgRuleCovers(rule, 22, "0.0.0.0/0"));
        Assert.False(AzureComputeClient.NsgRuleCovers(rule, 3724, "203.0.113.10/32"));
        Assert.False(AzureComputeClient.NsgRuleOpensPortPublicly(rule, 22));
    }

    [Fact]
    public void NsgRuleCovers_PublicCidrAndWildcard()
    {
        var rule = new AzureComputeClient.AzureNsgProbeRule
        {
            Name = "azp-tcp-8085",
            Protocol = "Tcp",
            DestinationPortRange = "8085-8101",
            SourceAddressPrefix = "0.0.0.0/0",
            Access = "Allow",
        };

        Assert.True(AzureComputeClient.NsgRuleCovers(rule, 8085, "0.0.0.0/0"));
        Assert.True(AzureComputeClient.NsgRuleCovers(rule, 8100, "203.0.113.10/32"));
        Assert.True(AzureComputeClient.NsgRuleOpensPortPublicly(rule, 8101));
        Assert.False(AzureComputeClient.NsgRuleOpensPortPublicly(rule, 3306));
    }

    [Fact]
    public void BuildNsgRuleName_IncludesPort()
    {
        var name = AzureComputeClient.BuildNsgRuleName(3724);
        Assert.Equal("azp-tcp-3724", name);
        Assert.StartsWith("azp-", AzureComputeClient.BuildNsgName("my-vm"), StringComparison.Ordinal);
    }
}

public sealed class HetznerFirewallRuleTests
{
    [Fact]
    public void FirewallRuleCovers_ExactPortAndCidr()
    {
        var rule = new HetznerCloudClient.HetznerFirewallInboundRule
        {
            Direction = "in",
            Protocol = "tcp",
            Port = "22",
            SourceIps = ["203.0.113.10/32"],
        };

        Assert.True(HetznerCloudClient.FirewallRuleCovers(rule, 22, "203.0.113.10/32"));
        Assert.False(HetznerCloudClient.FirewallRuleCovers(rule, 22, "0.0.0.0/0"));
        Assert.False(HetznerCloudClient.FirewallRuleCovers(rule, 3724, "203.0.113.10/32"));
        Assert.False(HetznerCloudClient.FirewallRuleOpensPortPublicly(rule, 22));
    }

    [Fact]
    public void FirewallRuleCovers_PublicCidr()
    {
        var rule = new HetznerCloudClient.HetznerFirewallInboundRule
        {
            Direction = "in",
            Protocol = "tcp",
            Port = "3724",
            SourceIps = ["0.0.0.0/0"],
        };

        Assert.True(HetznerCloudClient.FirewallRuleCovers(rule, 3724, "0.0.0.0/0"));
        Assert.True(HetznerCloudClient.FirewallRuleCovers(rule, 3724, "203.0.113.10/32"));
        Assert.True(HetznerCloudClient.FirewallRuleOpensPortPublicly(rule, 3724));
        Assert.False(HetznerCloudClient.FirewallRuleOpensPortPublicly(rule, 3306));
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

        var digitalOcean = Assert.Single(providers!, item => item.Provider == CloudProvider.DigitalOcean);
        Assert.Equal(CloudLoginMode.OAuth, digitalOcean.LoginMode);
        Assert.True(digitalOcean.IsImplemented);
        Assert.Equal("Sign in with DigitalOcean", digitalOcean.SignInLabel);

        var vultr = Assert.Single(providers!, item => item.Provider == CloudProvider.Vultr);
        Assert.Equal(CloudLoginMode.OAuth, vultr.LoginMode);
        Assert.True(vultr.IsImplemented);
        Assert.Equal("Sign in with Vultr", vultr.SignInLabel);

        var gcp = Assert.Single(providers!, item => item.Provider == CloudProvider.Gcp);
        Assert.Equal(CloudLoginMode.OAuth, gcp.LoginMode);
        Assert.True(gcp.IsImplemented);
        Assert.True(gcp.SupportsPkce);
        Assert.Equal("Sign in with Google Cloud", gcp.SignInLabel);

        var azure = Assert.Single(providers!, item => item.Provider == CloudProvider.Azure);
        Assert.Equal(CloudLoginMode.OAuth, azure.LoginMode);
        Assert.True(azure.IsImplemented);
        Assert.True(azure.SupportsPkce);
        Assert.Equal("Sign in with Microsoft", azure.SignInLabel);

        var hetzner = Assert.Single(providers!, item => item.Provider == CloudProvider.Hetzner);
        Assert.Equal(CloudLoginMode.GuidedToken, hetzner.LoginMode);
        Assert.True(hetzner.IsImplemented);
        Assert.True(hetzner.IsConfigured);
        Assert.Equal("Connect Hetzner project", hetzner.SignInLabel);
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
    public async Task StartDigitalOceanOAuth_WhenNotConfigured_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cloud/auth/DigitalOcean/start", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("API token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartDigitalOceanOAuth_WhenConfigured_ReturnsAuthorizeUrl()
    {
        var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("CloudOAuth:DigitalOcean:ClientId", "do-client-id");
                builder.UseSetting("CloudOAuth:DigitalOcean:ClientSecret", "do-client-secret");
            })
            .CreateClient();

        var response = await client.PostAsJsonAsync("/api/cloud/auth/DigitalOcean/start", new { label = "DO lab" });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudAuthStartResultDto>(JsonOptions);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.State));
        Assert.Contains("https://cloud.digitalocean.com/v1/oauth/authorize", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("client_id=do-client-id", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("response_type=code", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("scope=read%20write", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains(
            Uri.EscapeDataString("/api/cloud/auth/DigitalOcean/callback"),
            result.AuthorizationUrl,
            StringComparison.Ordinal);
        Assert.Contains($"state={Uri.EscapeDataString(result.State)}", result.AuthorizationUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartVultrOAuth_WhenNotConfigured_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cloud/auth/Vultr/start", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Client ID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartVultrOAuth_WhenConfigured_ReturnsAuthorizeUrl()
    {
        var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("CloudOAuth:Vultr:ClientId", "vultr-client-id");
                builder.UseSetting("CloudOAuth:Vultr:ClientSecret", "vultr-client-secret");
                builder.UseSetting("CloudOAuth:Vultr:ProviderId", "vultr-provider-id");
                builder.UseSetting("CloudOAuth:Vultr:AuthorizeUrl", "https://example.test/vultr/authorize");
            })
            .CreateClient();

        var response = await client.PostAsJsonAsync("/api/cloud/auth/Vultr/start", new { label = "Vultr lab" });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudAuthStartResultDto>(JsonOptions);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.State));
        Assert.StartsWith("https://example.test/vultr/authorize?", result.AuthorizationUrl);
        Assert.Contains("client_id=vultr-client-id", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("response_type=code", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains(
            Uri.EscapeDataString("/api/cloud/auth/Vultr/callback"),
            result.AuthorizationUrl,
            StringComparison.Ordinal);
        Assert.Contains($"state={Uri.EscapeDataString(result.State)}", result.AuthorizationUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartGcpOAuth_WhenNotConfigured_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cloud/auth/Gcp/start", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("service account JSON", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartGcpOAuth_WhenConfigured_ReturnsAuthorizeUrl()
    {
        var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("CloudOAuth:Gcp:ClientId", "gcp-client-id");
                builder.UseSetting("CloudOAuth:Gcp:ClientSecret", "gcp-client-secret");
            })
            .CreateClient();

        var response = await client.PostAsJsonAsync("/api/cloud/auth/Gcp/start", new { label = "GCP lab" });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudAuthStartResultDto>(JsonOptions);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.State));
        Assert.Contains("https://accounts.google.com/o/oauth2/v2/auth", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("client_id=gcp-client-id", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("response_type=code", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("access_type=offline", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("prompt=consent", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("code_challenge=", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains(
            Uri.EscapeDataString("/api/cloud/auth/Gcp/callback"),
            result.AuthorizationUrl,
            StringComparison.Ordinal);
        Assert.Contains($"state={Uri.EscapeDataString(result.State)}", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/compute"), result.AuthorizationUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAzureOAuth_WhenNotConfigured_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cloud/auth/Azure/start", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("service principal", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAzureOAuth_WhenConfigured_ReturnsAuthorizeUrl()
    {
        var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("CloudOAuth:Azure:ClientId", "azure-client-id");
                builder.UseSetting("CloudOAuth:Azure:ClientSecret", "azure-client-secret");
            })
            .CreateClient();

        var response = await client.PostAsJsonAsync("/api/cloud/auth/Azure/start", new { label = "Azure lab" });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudAuthStartResultDto>(JsonOptions);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.State));
        Assert.Contains("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("client_id=azure-client-id", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("response_type=code", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("code_challenge=", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains(
            Uri.EscapeDataString("/api/cloud/auth/Azure/callback"),
            result.AuthorizationUrl,
            StringComparison.Ordinal);
        Assert.Contains($"state={Uri.EscapeDataString(result.State)}", result.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("https://management.azure.com/.default"), result.AuthorizationUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartHetznerGuidedToken_ReturnsMessageWithoutAuthorizeUrl()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cloud/auth/Hetzner/start", new { label = "Hetzner lab" });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudAuthStartResultDto>(JsonOptions);
        Assert.NotNull(result);
        Assert.Null(result!.AuthorizationUrl);
        Assert.False(result.RequiresManualCredentials);
        Assert.Contains("Read & Write", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not OAuth", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteHetznerGuidedToken_WhenTokenMissing_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/cloud/auth/Hetzner/complete", new { label = "Hetzner lab" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("token", body, StringComparison.OrdinalIgnoreCase);
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
