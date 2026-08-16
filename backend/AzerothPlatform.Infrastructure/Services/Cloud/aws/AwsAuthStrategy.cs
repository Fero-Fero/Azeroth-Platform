using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class AwsAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly CloudOAuthOptions _options;
    private readonly AzerothCoreDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;
    private readonly AwsStsClient _awsStsClient;
    private readonly AwsEc2Client _awsEc2Client;
    private readonly ICloudProviderConnectionService _connectionService;
    private readonly IAwsCredentialResolver _awsCredentialResolver;

    public AwsAuthStrategy(
        IOptions<CloudOAuthOptions> options,
        AzerothCoreDbContext dbContext,
        ISecretProtector secretProtector,
        AwsStsClient awsStsClient,
        AwsEc2Client awsEc2Client,
        ICloudProviderConnectionService connectionService,
        IAwsCredentialResolver awsCredentialResolver)
    {
        _options = options.Value;
        _dbContext = dbContext;
        _secretProtector = secretProtector;
        _awsStsClient = awsStsClient;
        _awsEc2Client = awsEc2Client;
        _connectionService = connectionService;
        _awsCredentialResolver = awsCredentialResolver;
    }

    public override CloudProvider Provider => CloudProvider.Aws;

    public override CloudAuthProviderStatusDto GetStatus()
    {
        var configured = _options.Aws.IsConfigured;
        return new CloudAuthProviderStatusDto
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.AssumedRole,
            IsConfigured = configured,
            IsImplemented = true,
            SupportsPkce = false,
            SignInLabel = "Connect AWS account",
            UnavailableReason = configured
                ? string.Empty
                : "Connect AWS account will use this server's AWS credentials to detect the platform account. Set CloudOAuth:Aws:PlatformAccountId to pin it.",
        };
    }

    public override async Task<CloudAuthStartResultDto> StartAsync(
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var externalId = (request.ExternalId ?? string.Empty).Trim();
        var reconnectId = (request.ReconnectConnectionId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(externalId) && !string.IsNullOrWhiteSpace(reconnectId))
        {
            var entity = await _dbContext.CloudProviderConnections.AsNoTracking()
                .FirstOrDefaultAsync(connection => connection.Id == reconnectId, cancellationToken);
            if (entity is not null
                && CloudProviderCredentialStore.TryUnprotectAwsAssumedRole(
                    _secretProtector,
                    entity.ProtectedCredentials,
                    out var stored))
            {
                externalId = stored.ExternalId;
            }
        }

        if (string.IsNullOrWhiteSpace(externalId))
        {
            externalId = Guid.NewGuid().ToString("D");
        }

        var accountId = await ResolvePlatformAccountIdAsync(cancellationToken);
        var region = string.IsNullOrWhiteSpace(_options.Aws.Region) ? "us-east-1" : _options.Aws.Region.Trim();

        return new CloudAuthStartResultDto
        {
            ExternalId = externalId,
            CloudFormationConsoleUrl =
                $"https://{region}.console.aws.amazon.com/cloudformation/home?region={region}#/stacks/create/template",
            AwsTemplates = AwsIamConnectTemplate.BuildAll(accountId, externalId),
            Message =
                "Deploy the CloudFormation template in the AWS account you want to connect, then paste the Role ARN output.",
        };
    }

    public override async Task<CloudProviderConnectionDto> CompleteAsync(
        CloudAuthCompleteRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var roleArn = (request.RoleArn ?? string.Empty).Trim();
        var externalId = (request.ExternalId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(roleArn) || string.IsNullOrWhiteSpace(externalId))
        {
            throw new ArgumentException("Role ARN and External ID are required.");
        }

        if (!roleArn.StartsWith("arn:aws:iam::", StringComparison.Ordinal)
            || !roleArn.Contains(":role/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Enter a valid IAM role ARN (arn:aws:iam::<account-id>:role/<name>).");
        }

        var session = await _awsStsClient.AssumeRoleAsync(roleArn, externalId, cancellationToken);
        await _awsEc2Client.ValidateCredentialsAsync(session, cancellationToken);
        var accountId = await _awsStsClient.GetAccountIdAsync(session, cancellationToken);

        var label = string.IsNullOrWhiteSpace(request.Label) ? "AWS" : request.Label.Trim();
        var hint = string.IsNullOrWhiteSpace(accountId) ? roleArn : accountId;

        var connection = await _connectionService.UpsertOAuthConnectionAsync(
            new UpsertCloudOAuthConnectionRequestDto
            {
                Provider = CloudProvider.Aws,
                Label = label,
                AccountHint = hint,
                ProtectedCredentials = CloudProviderCredentialStore.ProtectAwsAssumedRole(
                    _secretProtector,
                    new CloudProviderCredentialStore.AwsAssumedRoleEnvelope
                    {
                        RoleArn = roleArn,
                        ExternalId = externalId,
                    }),
                ReconnectConnectionId = request.ReconnectConnectionId,
                DefaultRegion = request.DefaultRegion,
                AuthMethod = CloudAuthMethod.AssumedRole,
            },
            cancellationToken);

        _awsCredentialResolver.Invalidate(connection.Id);
        return connection;
    }

    public override async Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections.AsNoTracking()
                         .FirstOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");

        _awsCredentialResolver.Invalidate(connectionId);
        var credentials = await _awsCredentialResolver.ResolveAsync(entity, cancellationToken);
        await _awsEc2Client.ValidateCredentialsAsync(credentials, cancellationToken);
    }

    private async Task<string> ResolvePlatformAccountIdAsync(CancellationToken cancellationToken)
    {
        var configured = _options.Aws.PlatformAccountId.Trim();
        if (configured.Length == 12 && configured.All(char.IsDigit))
        {
            return configured;
        }

        var detected = await _awsStsClient.TryGetPlatformAccountIdAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(detected) && detected.Length == 12 && detected.All(char.IsDigit))
        {
            return detected;
        }

        throw new InvalidOperationException(
            "Set CloudOAuth:Aws:PlatformAccountId (12-digit account id) or configure AWS credentials on this server so Connect AWS account can create the trust role.");
    }
}
