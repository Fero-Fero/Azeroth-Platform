using System.Net;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Cloud;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services;

public sealed class CloudFirewallService : ICloudFirewallService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly AwsEc2Client _awsEc2Client;
    private readonly DigitalOceanClient _digitalOceanClient;
    private readonly VultrClient _vultrClient;
    private readonly GcpComputeClient _gcpComputeClient;
    private readonly AzureComputeClient _azureComputeClient;
    private readonly HetznerCloudClient _hetznerCloudClient;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly IAwsCredentialResolver _awsCredentialResolver;
    private readonly IDigitalOceanTokenResolver _digitalOceanTokenResolver;
    private readonly IVultrTokenResolver _vultrTokenResolver;
    private readonly IGcpCredentialResolver _gcpCredentialResolver;
    private readonly IAzureCredentialResolver _azureCredentialResolver;
    private readonly ISecretProtector _secretProtector;

    public CloudFirewallService(
        AzerothCoreDbContext dbContext,
        AwsEc2Client awsEc2Client,
        DigitalOceanClient digitalOceanClient,
        VultrClient vultrClient,
        GcpComputeClient gcpComputeClient,
        AzureComputeClient azureComputeClient,
        HetznerCloudClient hetznerCloudClient,
        ICloudAuditService cloudAuditService,
        IAwsCredentialResolver awsCredentialResolver,
        IDigitalOceanTokenResolver digitalOceanTokenResolver,
        IVultrTokenResolver vultrTokenResolver,
        IGcpCredentialResolver gcpCredentialResolver,
        IAzureCredentialResolver azureCredentialResolver,
        ISecretProtector secretProtector)
    {
        _dbContext = dbContext;
        _awsEc2Client = awsEc2Client;
        _digitalOceanClient = digitalOceanClient;
        _vultrClient = vultrClient;
        _gcpComputeClient = gcpComputeClient;
        _azureComputeClient = azureComputeClient;
        _hetznerCloudClient = hetznerCloudClient;
        _cloudAuditService = cloudAuditService;
        _awsCredentialResolver = awsCredentialResolver;
        _digitalOceanTokenResolver = digitalOceanTokenResolver;
        _vultrTokenResolver = vultrTokenResolver;
        _gcpCredentialResolver = gcpCredentialResolver;
        _azureCredentialResolver = azureCredentialResolver;
        _secretProtector = secretProtector;
    }

    public async Task<CloudFirewallApplyResultDto> ApplyStackSecurityGroupRulesAsync(
        string stackId,
        SyncCloudSecurityGroupRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(entity => entity.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException("Stack not found.");

        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            throw new InvalidOperationException("Cloud security group automation is only available for external VPC stacks.");
        }

        var publicHost = (stack.ExternalHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(publicHost))
        {
            throw new InvalidOperationException("This stack has no external host configured.");
        }

        var connectionId = (request.ConnectionId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A linked cloud connection is required.");
        }

        var adminCidr = ValidateAdminSourceCidr(request.AdminSourceCidr);

        var connection = await _dbContext.CloudProviderConnections.AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Id == connectionId, cancellationToken)
            ?? throw new KeyNotFoundException("Cloud connection not found.");

        if (!Enum.TryParse<CloudProvider>(connection.Provider, ignoreCase: true, out var provider)
            || provider is not (CloudProvider.Aws or CloudProvider.DigitalOcean or CloudProvider.Vultr or CloudProvider.Gcp or CloudProvider.Azure or CloudProvider.Hetzner))
        {
            throw new InvalidOperationException(
                $"{connection.Provider} connections do not support automated cloud firewall sync yet.");
        }

        var profile = VpcSecurityCatalog.BuildProfile(
            publicHost,
            stack.AuthServerPort,
            stack.WorldServerPort,
            stack.ArmoryPort,
            stack.ClientPort,
            stack.DatabasePort,
            stack.SoapPort,
            stack.ExternalSshPort);

        var instanceId = (request.InstanceId ?? string.Empty).Trim();
        var region = (request.Region ?? connection.DefaultRegion ?? string.Empty).Trim();

        return provider switch
        {
            CloudProvider.DigitalOcean => await ApplyDigitalOceanAsync(
                stackId,
                stack,
                connection,
                profile,
                adminCidr,
                publicHost,
                instanceId,
                connectionId,
                cancellationToken),
            CloudProvider.Vultr => await ApplyVultrAsync(
                stackId,
                stack,
                connection,
                profile,
                adminCidr,
                publicHost,
                instanceId,
                connectionId,
                cancellationToken),
            CloudProvider.Gcp => await ApplyGcpAsync(
                stackId,
                stack,
                connection,
                profile,
                adminCidr,
                publicHost,
                instanceId,
                connectionId,
                cancellationToken),
            CloudProvider.Azure => await ApplyAzureAsync(
                stackId,
                stack,
                connection,
                profile,
                adminCidr,
                publicHost,
                instanceId,
                connectionId,
                cancellationToken),
            CloudProvider.Hetzner => await ApplyHetznerAsync(
                stackId,
                stack,
                connection,
                profile,
                adminCidr,
                publicHost,
                instanceId,
                connectionId,
                cancellationToken),
            _ => await ApplyAwsAsync(
                stackId,
                connection,
                profile,
                adminCidr,
                publicHost,
                instanceId,
                region,
                connectionId,
                cancellationToken),
        };
    }

    private async Task<CloudFirewallApplyResultDto> ApplyAwsAsync(
        string stackId,
        CloudProviderConnectionEntity connection,
        VpcSecurityProfileDto profile,
        string adminCidr,
        string publicHost,
        string instanceId,
        string region,
        string connectionId,
        CancellationToken cancellationToken)
    {
        var ingressRules = profile.CloudSecurityGroupRules
            .Select(rule => new AwsEc2Client.AwsIngressRule
            {
                Port = rule.Port,
                Protocol = "tcp",
                Cidr = ResolveRuleCidr(rule.Source, adminCidr),
                Description = rule.Description ?? string.Empty,
            })
            .ToList();

        var credentials = await _awsCredentialResolver.ResolveAsync(connection, cancellationToken);
        var target = await _awsEc2Client.ResolveInstanceForFirewallAsync(
            credentials,
            publicHost,
            string.IsNullOrWhiteSpace(region) ? null : region,
            string.IsNullOrWhiteSpace(instanceId) ? null : instanceId,
            cancellationToken);

        var (applied, skipped) = await _awsEc2Client.ApplySecurityGroupIngressRulesAsync(
            credentials,
            target.Region,
            target.SecurityGroupIds,
            ingressRules,
            cancellationToken);

        var message = applied > 0
            ? $"Applied {applied} ingress rule(s) to {target.SecurityGroupIds.Count} AWS security group(s). Skipped {skipped} duplicate rule(s)."
            : skipped > 0
                ? $"All {skipped} rule(s) were already present on the instance security group(s)."
                : "No security group rules were applied.";

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.CloudFirewallApplied,
                ResourceType = "stack",
                ResourceId = stackId,
                Summary = message,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    connectionId,
                    provider = CloudProvider.Aws.ToString(),
                    instanceId = target.InstanceId,
                    region = target.Region,
                    publicHost = target.PublicHost,
                    adminSourceCidr = adminCidr,
                    rulesApplied = applied,
                    rulesSkipped = skipped,
                    securityGroupIds = target.SecurityGroupIds,
                }),
            },
            cancellationToken);

        return new CloudFirewallApplyResultDto
        {
            Success = true,
            Message = message,
            Provider = CloudProvider.Aws,
            RulesApplied = applied,
            RulesSkipped = skipped,
            SecurityGroupIds = target.SecurityGroupIds,
        };
    }

    private async Task<CloudFirewallApplyResultDto> ApplyDigitalOceanAsync(
        string stackId,
        ManagedStackEntity stack,
        CloudProviderConnectionEntity connection,
        VpcSecurityProfileDto profile,
        string adminCidr,
        string publicHost,
        string instanceId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        var accessToken = await _digitalOceanTokenResolver.ResolveAsync(connection, cancellationToken);
        var droplet = await _digitalOceanClient.FindDropletAsync(
            accessToken,
            instanceId,
            publicHost,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Could not find a DigitalOcean droplet matching this stack's instance id or public IP.");

        var inbound = profile.CloudSecurityGroupRules
            .Select(rule => new DigitalOceanClient.DigitalOceanFirewallInboundRule
            {
                Protocol = "tcp",
                Ports = rule.Port.ToString(),
                SourceAddresses = [ResolveRuleCidr(rule.Source, adminCidr)],
            })
            .ToList();

        var firewallName = string.IsNullOrWhiteSpace(stack.Id)
            ? $"azeroth-platform-{droplet.Id}"
            : $"azeroth-platform-{stack.Id}";
        var firewall = await _digitalOceanClient.ApplyDropletFirewallAsync(
            accessToken,
            firewallName,
            droplet.Id,
            inbound,
            cancellationToken);

        var message = $"Applied {inbound.Count} inbound rule(s) on DigitalOcean Cloud Firewall {firewall.Name}.";
        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.CloudFirewallApplied,
                ResourceType = "stack",
                ResourceId = stackId,
                Summary = message,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    connectionId,
                    provider = CloudProvider.DigitalOcean.ToString(),
                    instanceId = droplet.Id.ToString(),
                    publicHost,
                    adminSourceCidr = adminCidr,
                    rulesApplied = inbound.Count,
                    firewallId = firewall.Id,
                }),
            },
            cancellationToken);

        return new CloudFirewallApplyResultDto
        {
            Success = true,
            Message = message,
            Provider = CloudProvider.DigitalOcean,
            RulesApplied = inbound.Count,
            RulesSkipped = 0,
            SecurityGroupIds = string.IsNullOrWhiteSpace(firewall.Id) ? [] : [firewall.Id],
        };
    }

    private async Task<CloudFirewallApplyResultDto> ApplyVultrAsync(
        string stackId,
        ManagedStackEntity stack,
        CloudProviderConnectionEntity connection,
        VpcSecurityProfileDto profile,
        string adminCidr,
        string publicHost,
        string instanceId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        var accessToken = await _vultrTokenResolver.ResolveAsync(connection, cancellationToken);
        var instance = await _vultrClient.FindInstanceAsync(
            accessToken,
            instanceId,
            publicHost,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Could not find a Vultr instance matching this stack's instance id or public IP.");

        var inbound = profile.CloudSecurityGroupRules
            .Select(rule =>
            {
                var cidr = ResolveRuleCidr(rule.Source, adminCidr);
                var (subnet, size) = VultrClient.SplitCidr(cidr);
                return new VultrClient.VultrFirewallInboundRule
                {
                    Protocol = "tcp",
                    Port = rule.Port.ToString(),
                    Subnet = subnet,
                    SubnetSize = size,
                    Notes = rule.Description,
                };
            })
            .ToList();

        var description = string.IsNullOrWhiteSpace(stack.Id)
            ? $"azeroth-platform-{instance.Id}"
            : $"azeroth-platform-{stack.Id}";
        var firewall = await _vultrClient.ApplyFirewallGroupAsync(
            accessToken,
            description,
            instance.Id,
            inbound,
            cancellationToken);

        var message = $"Applied {inbound.Count} inbound rule(s) on Vultr firewall group {firewall.Description}.";
        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.CloudFirewallApplied,
                ResourceType = "stack",
                ResourceId = stackId,
                Summary = message,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    connectionId,
                    provider = CloudProvider.Vultr.ToString(),
                    instanceId = instance.Id,
                    publicHost,
                    adminSourceCidr = adminCidr,
                    rulesApplied = inbound.Count,
                    firewallId = firewall.Id,
                }),
            },
            cancellationToken);

        return new CloudFirewallApplyResultDto
        {
            Success = true,
            Message = message,
            Provider = CloudProvider.Vultr,
            RulesApplied = inbound.Count,
            RulesSkipped = 0,
            SecurityGroupIds = string.IsNullOrWhiteSpace(firewall.Id) ? [] : [firewall.Id],
        };
    }

    private async Task<CloudFirewallApplyResultDto> ApplyGcpAsync(
        string stackId,
        ManagedStackEntity stack,
        CloudProviderConnectionEntity connection,
        VpcSecurityProfileDto profile,
        string adminCidr,
        string publicHost,
        string instanceId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        var access = await _gcpCredentialResolver.ResolveAsync(connection, cancellationToken);
        var instance = await _gcpComputeClient.FindInstanceAsync(
            access,
            instanceId,
            publicHost,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Could not find a GCP VM matching this stack's instance id or public IP.");

        var inbound = profile.CloudSecurityGroupRules
            .Select(rule => new GcpComputeClient.GcpFirewallInboundRule
            {
                Port = rule.Port,
                SourceCidr = ResolveRuleCidr(rule.Source, adminCidr),
                Description = rule.Description,
            })
            .ToList();

        var resourceId = string.IsNullOrWhiteSpace(stack.Id) ? instance.Name : stack.Id;
        var applied = await _gcpComputeClient.ApplyFirewallRulesAsync(
            access,
            resourceId,
            instance,
            inbound,
            cancellationToken);

        var message = $"Applied {applied} ingress rule(s) on GCP VPC firewall targeting tag {GcpComputeClient.PlatformNetworkTag}.";
        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.CloudFirewallApplied,
                ResourceType = "stack",
                ResourceId = stackId,
                Summary = message,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    connectionId,
                    provider = CloudProvider.Gcp.ToString(),
                    instanceId = instance.Id,
                    publicHost,
                    adminSourceCidr = adminCidr,
                    rulesApplied = applied,
                    networkTag = GcpComputeClient.PlatformNetworkTag,
                }),
            },
            cancellationToken);

        return new CloudFirewallApplyResultDto
        {
            Success = true,
            Message = message,
            Provider = CloudProvider.Gcp,
            RulesApplied = applied,
            RulesSkipped = 0,
            SecurityGroupIds = [GcpComputeClient.PlatformNetworkTag],
        };
    }

    private async Task<CloudFirewallApplyResultDto> ApplyAzureAsync(
        string stackId,
        ManagedStackEntity stack,
        CloudProviderConnectionEntity connection,
        VpcSecurityProfileDto profile,
        string adminCidr,
        string publicHost,
        string instanceId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        var access = await _azureCredentialResolver.ResolveAsync(connection, cancellationToken);
        var instance = await _azureComputeClient.FindInstanceAsync(
            access,
            instanceId,
            publicHost,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Could not find an Azure VM matching this stack's instance id or public IP.");

        var inbound = profile.CloudSecurityGroupRules
            .Select(rule => new AzureComputeClient.AzureNsgInboundRule
            {
                Port = rule.Port,
                SourceCidr = ResolveRuleCidr(rule.Source, adminCidr),
                Description = rule.Description,
            })
            .ToList();

        var (applied, nsgId) = await _azureComputeClient.ApplyNsgRulesAsync(
            access,
            instance.Id,
            inbound,
            cancellationToken);

        var message = $"Applied {applied} inbound rule(s) on Azure NSG for VM {instance.Name}.";
        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.CloudFirewallApplied,
                ResourceType = "stack",
                ResourceId = stackId,
                Summary = message,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    connectionId,
                    provider = CloudProvider.Azure.ToString(),
                    instanceId = instance.Id,
                    publicHost,
                    adminSourceCidr = adminCidr,
                    rulesApplied = applied,
                    nsgId,
                }),
            },
            cancellationToken);

        return new CloudFirewallApplyResultDto
        {
            Success = true,
            Message = message,
            Provider = CloudProvider.Azure,
            RulesApplied = applied,
            RulesSkipped = 0,
            SecurityGroupIds = string.IsNullOrWhiteSpace(nsgId) ? [] : [nsgId],
        };
    }

    private async Task<CloudFirewallApplyResultDto> ApplyHetznerAsync(
        string stackId,
        ManagedStackEntity stack,
        CloudProviderConnectionEntity connection,
        VpcSecurityProfileDto profile,
        string adminCidr,
        string publicHost,
        string instanceId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        var accessToken = CloudProviderCredentialStore.UnprotectApiToken(
            _secretProtector,
            connection.ProtectedCredentials);
        var server = await _hetznerCloudClient.FindServerAsync(
            accessToken,
            instanceId,
            publicHost,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Could not find a Hetzner server matching this stack's instance id or public IP.");

        var inbound = profile.CloudSecurityGroupRules
            .Select(rule => new HetznerCloudClient.HetznerFirewallInboundRule
            {
                Port = rule.Port.ToString(),
                SourceIps = [ResolveRuleCidr(rule.Source, adminCidr)],
                Description = rule.Description,
            })
            .ToList();

        var firewallName = string.IsNullOrWhiteSpace(stack.Id)
            ? $"azeroth-platform-{server.Id}"
            : $"azeroth-platform-{stack.Id}";
        var firewall = await _hetznerCloudClient.ApplyFirewallAsync(
            accessToken,
            firewallName,
            server.Id,
            inbound,
            cancellationToken);

        var message = $"Applied {inbound.Count} inbound rule(s) on Hetzner Cloud Firewall {firewall.Name}.";
        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.CloudFirewallApplied,
                ResourceType = "stack",
                ResourceId = stackId,
                Summary = message,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    connectionId,
                    provider = CloudProvider.Hetzner.ToString(),
                    instanceId = server.Id.ToString(),
                    publicHost,
                    adminSourceCidr = adminCidr,
                    rulesApplied = inbound.Count,
                    firewallId = firewall.Id,
                }),
            },
            cancellationToken);

        return new CloudFirewallApplyResultDto
        {
            Success = true,
            Message = message,
            Provider = CloudProvider.Hetzner,
            RulesApplied = inbound.Count,
            RulesSkipped = 0,
            SecurityGroupIds = firewall.Id == 0 ? [] : [firewall.Id.ToString()],
        };
    }

    public async Task<CloudFirewallProbeResultDto> ProbeLaunchSecurityGroupAsync(
        string connectionId,
        CloudFirewallProbeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var host = (request.PublicHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Public host is required.");
        }

        var connection = await _dbContext.CloudProviderConnections.AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Id == connectionId, cancellationToken)
            ?? throw new KeyNotFoundException("Cloud connection not found.");

        if (!Enum.TryParse<CloudProvider>(connection.Provider, ignoreCase: true, out var provider)
            || provider is not (CloudProvider.Aws or CloudProvider.DigitalOcean or CloudProvider.Vultr or CloudProvider.Gcp or CloudProvider.Azure or CloudProvider.Hetzner))
        {
            return new CloudFirewallProbeResultDto
            {
                Success = false,
                Message = $"Cloud firewall probe is not implemented for {connection.Provider}.",
                Checks =
                [
                    new RemotePrerequisiteCheckDto
                    {
                        Name = "Cloud firewall",
                        Passed = false,
                        Message = $"Automated cloud firewall checks are not implemented for {connection.Provider}."
                    }
                ]
            };
        }

        if (provider == CloudProvider.DigitalOcean)
        {
            return await ProbeDigitalOceanAsync(connection, request, cancellationToken);
        }

        if (provider == CloudProvider.Vultr)
        {
            return await ProbeVultrAsync(connection, request, cancellationToken);
        }

        if (provider == CloudProvider.Gcp)
        {
            return await ProbeGcpAsync(connection, request, cancellationToken);
        }

        if (provider == CloudProvider.Azure)
        {
            return await ProbeAzureAsync(connection, request, cancellationToken);
        }

        if (provider == CloudProvider.Hetzner)
        {
            return await ProbeHetznerAsync(connection, request, cancellationToken);
        }

        var credentials = await _awsCredentialResolver.ResolveAsync(connection, cancellationToken);
        var actual = await _awsEc2Client.ListInstanceIngressRulesAsync(
            credentials,
            host,
            string.IsNullOrWhiteSpace(request.Region) ? connection.DefaultRegion : request.Region,
            request.InstanceId,
            cancellationToken);

        var expected = VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr);
        var checks = new List<RemotePrerequisiteCheckDto>();
        foreach (var rule in expected)
        {
            var present = actual.Any(item =>
                item.Port == rule.Port
                && string.Equals(item.Protocol, "tcp", StringComparison.OrdinalIgnoreCase)
                && CidrCovers(item.Cidr, rule.Source));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"AWS SG tcp/{rule.Port}",
                Passed = present,
                Message = present
                    ? $"{rule.Description} is open in the instance security group."
                    : $"{rule.Description} (tcp/{rule.Port} from {rule.Source}) is missing from the instance security group."
            });
        }

        foreach (var denied in new (int Port, string Label)[] { (3306, "MySQL"), (7878, "SOAP") })
        {
            var publicOpen = actual.Any(item =>
                item.Port == denied.Port
                && (item.Cidr == "0.0.0.0/0" || item.Cidr == "::/0"));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"AWS SG deny {denied.Port}/tcp ({denied.Label})",
                Passed = !publicOpen,
                Message = publicOpen
                    ? $"{denied.Label} is open to the internet on the instance security group."
                    : $"{denied.Label} is not publicly opened (expected)."
            });
        }

        var success = checks.All(check => check.Passed);
        return new CloudFirewallProbeResultDto
        {
            Success = success,
            Message = success
                ? "Cloud security group matches the launch profile."
                : "Cloud security group does not yet match the launch profile.",
            Checks = checks,
        };
    }

    private async Task<CloudFirewallProbeResultDto> ProbeDigitalOceanAsync(
        CloudProviderConnectionEntity connection,
        CloudFirewallProbeRequestDto request,
        CancellationToken cancellationToken)
    {
        var accessToken = await _digitalOceanTokenResolver.ResolveAsync(connection, cancellationToken);
        var droplet = await _digitalOceanClient.FindDropletAsync(
            accessToken,
            request.InstanceId,
            request.PublicHost,
            cancellationToken);
        if (droplet is null)
        {
            return new CloudFirewallProbeResultDto
            {
                Success = false,
                Message = "Could not find a DigitalOcean droplet matching this host.",
                Checks =
                [
                    new RemotePrerequisiteCheckDto
                    {
                        Name = "DigitalOcean Cloud Firewall",
                        Passed = false,
                        Message = "No droplet matched the public IP or instance id, so firewall rules could not be probed."
                    }
                ]
            };
        }

        var actual = await _digitalOceanClient.ListDropletInboundRulesAsync(
            accessToken,
            droplet.Id,
            cancellationToken);
        var expected = VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr);
        var checks = new List<RemotePrerequisiteCheckDto>();
        foreach (var rule in expected)
        {
            var present = actual.Any(item =>
                DigitalOceanClient.InboundRuleCovers(item, rule.Port, rule.Source));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"DO firewall tcp/{rule.Port}",
                Passed = present,
                Message = present
                    ? $"{rule.Description} is open on the droplet Cloud Firewall."
                    : $"{rule.Description} (tcp/{rule.Port} from {rule.Source}) is missing from the droplet Cloud Firewall."
            });
        }

        foreach (var denied in new (int Port, string Label)[] { (3306, "MySQL"), (7878, "SOAP") })
        {
            var publicOpen = actual.Any(item =>
                DigitalOceanClient.InboundRuleOpensPortPublicly(item, denied.Port));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"DO firewall deny {denied.Port}/tcp ({denied.Label})",
                Passed = !publicOpen,
                Message = publicOpen
                    ? $"{denied.Label} is open to the internet on the droplet Cloud Firewall."
                    : $"{denied.Label} is not publicly opened (expected)."
            });
        }

        var success = checks.All(check => check.Passed);
        return new CloudFirewallProbeResultDto
        {
            Success = success,
            Message = success
                ? "DigitalOcean Cloud Firewall matches the launch profile."
                : "DigitalOcean Cloud Firewall does not yet match the launch profile.",
            Checks = checks,
        };
    }

    private async Task<CloudFirewallProbeResultDto> ProbeVultrAsync(
        CloudProviderConnectionEntity connection,
        CloudFirewallProbeRequestDto request,
        CancellationToken cancellationToken)
    {
        var accessToken = await _vultrTokenResolver.ResolveAsync(connection, cancellationToken);
        var instance = await _vultrClient.FindInstanceAsync(
            accessToken,
            request.InstanceId,
            request.PublicHost,
            cancellationToken);
        if (instance is null)
        {
            return new CloudFirewallProbeResultDto
            {
                Success = false,
                Message = "Could not find a Vultr instance matching this host.",
                Checks =
                [
                    new RemotePrerequisiteCheckDto
                    {
                        Name = "Vultr firewall group",
                        Passed = false,
                        Message = "No instance matched the public IP or instance id, so firewall rules could not be probed."
                    }
                ]
            };
        }

        var actual = await _vultrClient.ListInstanceFirewallRulesAsync(
            accessToken,
            instance.Id,
            cancellationToken);
        var expected = VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr);
        var checks = new List<RemotePrerequisiteCheckDto>();
        foreach (var rule in expected)
        {
            var present = actual.Any(item =>
                VultrClient.FirewallRuleCovers(item, rule.Port, rule.Source));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"Vultr firewall tcp/{rule.Port}",
                Passed = present,
                Message = present
                    ? $"{rule.Description} is open on the instance firewall group."
                    : $"{rule.Description} (tcp/{rule.Port} from {rule.Source}) is missing from the instance firewall group."
            });
        }

        foreach (var denied in new (int Port, string Label)[] { (3306, "MySQL"), (7878, "SOAP") })
        {
            var publicOpen = actual.Any(item =>
                VultrClient.FirewallRuleOpensPortPublicly(item, denied.Port));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"Vultr firewall deny {denied.Port}/tcp ({denied.Label})",
                Passed = !publicOpen,
                Message = publicOpen
                    ? $"{denied.Label} is open to the internet on the instance firewall group."
                    : $"{denied.Label} is not publicly opened (expected)."
            });
        }

        var success = checks.All(check => check.Passed);
        return new CloudFirewallProbeResultDto
        {
            Success = success,
            Message = success
                ? "Vultr firewall group matches the launch profile."
                : "Vultr firewall group does not yet match the launch profile.",
            Checks = checks,
        };
    }

    private async Task<CloudFirewallProbeResultDto> ProbeGcpAsync(
        CloudProviderConnectionEntity connection,
        CloudFirewallProbeRequestDto request,
        CancellationToken cancellationToken)
    {
        var access = await _gcpCredentialResolver.ResolveAsync(connection, cancellationToken);
        var instance = await _gcpComputeClient.FindInstanceAsync(
            access,
            request.InstanceId,
            request.PublicHost,
            cancellationToken);
        if (instance is null)
        {
            return new CloudFirewallProbeResultDto
            {
                Success = false,
                Message = "Could not find a GCP VM matching this host.",
                Checks =
                [
                    new RemotePrerequisiteCheckDto
                    {
                        Name = "GCP VPC firewall",
                        Passed = false,
                        Message = "No VM matched the public IP or instance id, so firewall rules could not be probed."
                    }
                ]
            };
        }

        var actual = await _gcpComputeClient.ListInstanceFirewallRulesAsync(
            access,
            instance,
            cancellationToken);
        var hasTag = instance.HasPlatformTag || actual.Any(rule => rule.InstanceHasPlatformTag);
        var checks = new List<RemotePrerequisiteCheckDto>
        {
            new()
            {
                Name = $"GCP network tag {GcpComputeClient.PlatformNetworkTag}",
                Passed = hasTag,
                Message = hasTag
                    ? $"Instance has network tag {GcpComputeClient.PlatformNetworkTag}."
                    : $"Instance is missing network tag {GcpComputeClient.PlatformNetworkTag}; VPC firewall rules will not apply."
            }
        };

        var expected = VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr);
        foreach (var rule in expected)
        {
            var present = actual.Any(item =>
                GcpComputeClient.FirewallRuleCovers(item, rule.Port, rule.Source));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"GCP firewall tcp/{rule.Port}",
                Passed = present,
                Message = present
                    ? $"{rule.Description} is open on VPC firewall rules targeting {GcpComputeClient.PlatformNetworkTag}."
                    : $"{rule.Description} (tcp/{rule.Port} from {rule.Source}) is missing from VPC firewall rules."
            });
        }

        foreach (var denied in new (int Port, string Label)[] { (3306, "MySQL"), (7878, "SOAP") })
        {
            var publicOpen = actual.Any(item =>
                GcpComputeClient.FirewallRuleOpensPortPublicly(item, denied.Port));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"GCP firewall deny {denied.Port}/tcp ({denied.Label})",
                Passed = !publicOpen,
                Message = publicOpen
                    ? $"{denied.Label} is open to the internet on the VPC firewall."
                    : $"{denied.Label} is not publicly opened (expected)."
            });
        }

        var success = checks.All(check => check.Passed);
        return new CloudFirewallProbeResultDto
        {
            Success = success,
            Message = success
                ? "GCP VPC firewall and instance tag match the launch profile."
                : "GCP VPC firewall does not yet match the launch profile.",
            Checks = checks,
        };
    }

    private async Task<CloudFirewallProbeResultDto> ProbeAzureAsync(
        CloudProviderConnectionEntity connection,
        CloudFirewallProbeRequestDto request,
        CancellationToken cancellationToken)
    {
        var access = await _azureCredentialResolver.ResolveAsync(connection, cancellationToken);
        var instance = await _azureComputeClient.FindInstanceAsync(
            access,
            request.InstanceId,
            request.PublicHost,
            cancellationToken);
        if (instance is null)
        {
            return new CloudFirewallProbeResultDto
            {
                Success = false,
                Message = "Could not find an Azure VM matching this host.",
                Checks =
                [
                    new RemotePrerequisiteCheckDto
                    {
                        Name = "Azure NSG",
                        Passed = false,
                        Message = "No VM matched the public IP or instance id, so NSG rules could not be probed."
                    }
                ]
            };
        }

        var actual = await _azureComputeClient.ListNsgInboundRulesAsync(
            access,
            instance.Id,
            cancellationToken);
        var checks = new List<RemotePrerequisiteCheckDto>();
        if (actual.Count == 0)
        {
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = "Azure NSG attached",
                Passed = false,
                Message = "The VM NIC has no network security group, so inbound rules could not be verified."
            });
        }

        var expected = VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr);
        foreach (var rule in expected)
        {
            var present = actual.Any(item =>
                AzureComputeClient.NsgRuleCovers(item, rule.Port, rule.Source));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"Azure NSG tcp/{rule.Port}",
                Passed = present,
                Message = present
                    ? $"{rule.Description} is open on the attached NSG."
                    : $"{rule.Description} (tcp/{rule.Port} from {rule.Source}) is missing from the NSG."
            });
        }

        foreach (var denied in new (int Port, string Label)[] { (3306, "MySQL"), (7878, "SOAP") })
        {
            var publicOpen = actual.Any(item =>
                AzureComputeClient.NsgRuleOpensPortPublicly(item, denied.Port));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"Azure NSG deny {denied.Port}/tcp ({denied.Label})",
                Passed = !publicOpen,
                Message = publicOpen
                    ? $"{denied.Label} is open to the internet on the NSG."
                    : $"{denied.Label} is not publicly opened (expected)."
            });
        }

        var success = checks.Count > 0 && checks.All(check => check.Passed);
        return new CloudFirewallProbeResultDto
        {
            Success = success,
            Message = success
                ? "Azure NSG matches the launch profile."
                : "Azure NSG does not yet match the launch profile.",
            Checks = checks,
        };
    }

    private async Task<CloudFirewallProbeResultDto> ProbeHetznerAsync(
        CloudProviderConnectionEntity connection,
        CloudFirewallProbeRequestDto request,
        CancellationToken cancellationToken)
    {
        var accessToken = CloudProviderCredentialStore.UnprotectApiToken(
            _secretProtector,
            connection.ProtectedCredentials);
        var server = await _hetznerCloudClient.FindServerAsync(
            accessToken,
            request.InstanceId,
            request.PublicHost,
            cancellationToken);
        if (server is null)
        {
            return new CloudFirewallProbeResultDto
            {
                Success = false,
                Message = "Could not find a Hetzner server matching this host.",
                Checks =
                [
                    new RemotePrerequisiteCheckDto
                    {
                        Name = "Hetzner Cloud Firewall",
                        Passed = false,
                        Message = "No server matched the public IP or instance id, so firewall rules could not be probed."
                    }
                ]
            };
        }

        var actual = await _hetznerCloudClient.ListServerInboundRulesAsync(
            accessToken,
            server.Id,
            cancellationToken);
        var expected = VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr);
        var checks = new List<RemotePrerequisiteCheckDto>();
        foreach (var rule in expected)
        {
            var present = actual.Any(item =>
                HetznerCloudClient.FirewallRuleCovers(item, rule.Port, rule.Source));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"Hetzner firewall tcp/{rule.Port}",
                Passed = present,
                Message = present
                    ? $"{rule.Description} is open on the server Cloud Firewall."
                    : $"{rule.Description} (tcp/{rule.Port} from {rule.Source}) is missing from the server Cloud Firewall."
            });
        }

        foreach (var denied in new (int Port, string Label)[] { (3306, "MySQL"), (7878, "SOAP") })
        {
            var publicOpen = actual.Any(item =>
                HetznerCloudClient.FirewallRuleOpensPortPublicly(item, denied.Port));
            checks.Add(new RemotePrerequisiteCheckDto
            {
                Name = $"Hetzner firewall deny {denied.Port}/tcp ({denied.Label})",
                Passed = !publicOpen,
                Message = publicOpen
                    ? $"{denied.Label} is open to the internet on the server Cloud Firewall."
                    : $"{denied.Label} is not publicly opened (expected)."
            });
        }

        var success = checks.All(check => check.Passed);
        return new CloudFirewallProbeResultDto
        {
            Success = success,
            Message = success
                ? "Hetzner Cloud Firewall matches the launch profile."
                : "Hetzner Cloud Firewall does not yet match the launch profile.",
            Checks = checks,
        };
    }

    private static bool CidrCovers(string actualCidr, string expectedCidr)
    {
        var actual = (actualCidr ?? string.Empty).Trim();
        var expected = (expectedCidr ?? string.Empty).Trim();
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return actual is "0.0.0.0/0" or "::/0";
    }

    private static string ValidateAdminSourceCidr(string? value)
    {
        var cidr = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cidr))
        {
            throw new ArgumentException("Admin source CIDR is required (for example 203.0.113.10/32).");
        }

        var slashIndex = cidr.LastIndexOf('/');
        if (slashIndex <= 0 || slashIndex >= cidr.Length - 1)
        {
            throw new ArgumentException("Admin source CIDR must include a prefix length, for example 203.0.113.10/32.");
        }

        var addressPart = cidr[..slashIndex];
        if (!IPAddress.TryParse(addressPart, out _))
        {
            throw new ArgumentException("Admin source CIDR contains an invalid IP address.");
        }

        if (!int.TryParse(cidr[(slashIndex + 1)..], out var prefixLength)
            || prefixLength is < 0 or > 32)
        {
            throw new ArgumentException("Admin source CIDR prefix length must be between 0 and 32.");
        }

        return cidr;
    }

    private static string ResolveRuleCidr(string? template, string adminCidr)
    {
        var source = (template ?? string.Empty).Trim();
        if (string.Equals(source, "your-ip/32", StringComparison.OrdinalIgnoreCase))
        {
            return adminCidr;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException("Cloud security group profile rule is missing a source CIDR.");
        }

        return source;
    }
}
