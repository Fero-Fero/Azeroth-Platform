using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class AwsStsClient
{
    private readonly CloudOAuthOptions _options;

    public AwsStsClient(IOptions<CloudOAuthOptions> options)
    {
        _options = options.Value;
    }

    public async Task<AwsRuntimeCredentials> AssumeRoleAsync(
        string roleArn,
        string externalId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        try
        {
            var response = await client.AssumeRoleAsync(
                new AssumeRoleRequest
                {
                    RoleArn = roleArn.Trim(),
                    RoleSessionName = "AzerothPlatform",
                    ExternalId = externalId.Trim(),
                    DurationSeconds = 3600,
                },
                cancellationToken);

            var credentials = response.Credentials
                              ?? throw new InvalidOperationException("AWS STS did not return credentials.");

            return new AwsRuntimeCredentials
            {
                AccessKeyId = credentials.AccessKeyId,
                SecretAccessKey = credentials.SecretAccessKey,
                SessionToken = credentials.SessionToken,
                ExpiresAtUtc = credentials.Expiration.ToUniversalTime(),
            };
        }
        catch (AmazonServiceException ex)
        {
            throw new InvalidOperationException(ParseStsError(ex));
        }
    }

    public async Task<string> GetAccountIdAsync(
        AwsRuntimeCredentials credentials,
        CancellationToken cancellationToken)
    {
        var region = string.IsNullOrWhiteSpace(_options.Aws.Region) ? "us-east-1" : _options.Aws.Region.Trim();
        using var client = new AmazonSecurityTokenServiceClient(
            credentials.ToSdk(),
            new AmazonSecurityTokenServiceConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            });

        try
        {
            var identity = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
            return identity.Account ?? string.Empty;
        }
        catch (AmazonServiceException ex)
        {
            throw new InvalidOperationException(ParseStsError(ex));
        }
    }

    public async Task<string?> TryGetPlatformAccountIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var identity = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(), timeout.Token);
            var account = (identity.Account ?? string.Empty).Trim();
            return account.Length == 12 && account.All(char.IsDigit) ? account : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private AmazonSecurityTokenServiceClient CreateClient()
    {
        var region = string.IsNullOrWhiteSpace(_options.Aws.Region) ? "us-east-1" : _options.Aws.Region.Trim();
        var config = new AmazonSecurityTokenServiceConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
        };

        var accessKeyId = _options.Aws.AccessKeyId.Trim();
        var secretAccessKey = _options.Aws.SecretAccessKey.Trim();
        if (!string.IsNullOrWhiteSpace(accessKeyId) && !string.IsNullOrWhiteSpace(secretAccessKey))
        {
            return new AmazonSecurityTokenServiceClient(
                new BasicAWSCredentials(accessKeyId, secretAccessKey),
                config);
        }

        return new AmazonSecurityTokenServiceClient(config);
    }

    private static string ParseStsError(AmazonServiceException exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "AWS STS rejected the request."
            : exception.Message.Length <= 400
                ? exception.Message
                : exception.Message[..400] + "…";

        if (string.Equals(exception.ErrorCode, "AccessDenied", StringComparison.OrdinalIgnoreCase))
        {
            return "AWS denied AssumeRole. Check the role ARN, External ID, and that the role trusts this platform account.";
        }

        return message;
    }
}
