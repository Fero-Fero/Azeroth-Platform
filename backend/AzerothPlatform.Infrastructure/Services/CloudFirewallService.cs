using System.Net;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services.Cloud;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services;

public sealed class CloudFirewallService : ICloudFirewallService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly AwsEc2Client _awsEc2Client;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly IAwsCredentialResolver _awsCredentialResolver;

    public CloudFirewallService(
        AzerothCoreDbContext dbContext,
        AwsEc2Client awsEc2Client,
        ICloudAuditService cloudAuditService,
        IAwsCredentialResolver awsCredentialResolver)
    {
        _dbContext = dbContext;
        _awsEc2Client = awsEc2Client;
        _cloudAuditService = cloudAuditService;
        _awsCredentialResolver = awsCredentialResolver;
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
            || provider != CloudProvider.Aws)
        {
            throw new InvalidOperationException("Only AWS connections support automated security group sync today.");
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

        var instanceId = (request.InstanceId ?? string.Empty).Trim();
        var region = (request.Region ?? connection.DefaultRegion ?? string.Empty).Trim();

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
            || provider != CloudProvider.Aws)
        {
            return new CloudFirewallProbeResultDto
            {
                Success = false,
                Message = "Cloud security group probe currently supports AWS only.",
                Checks =
                [
                    new RemotePrerequisiteCheckDto
                    {
                        Name = "Cloud security group",
                        Passed = false,
                        Message = "Automated security group checks are only implemented for AWS."
                    }
                ]
            };
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
