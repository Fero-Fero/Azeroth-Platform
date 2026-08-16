using Amazon.Runtime;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class AwsRuntimeCredentials
{
    public string AccessKeyId { get; init; } = string.Empty;

    public string SecretAccessKey { get; init; } = string.Empty;

    public string? SessionToken { get; init; }

    public DateTime? ExpiresAtUtc { get; init; }

    public AWSCredentials ToSdk()
        => string.IsNullOrWhiteSpace(SessionToken)
            ? new BasicAWSCredentials(AccessKeyId.Trim(), SecretAccessKey.Trim())
            : new SessionAWSCredentials(AccessKeyId.Trim(), SecretAccessKey.Trim(), SessionToken.Trim());
}
