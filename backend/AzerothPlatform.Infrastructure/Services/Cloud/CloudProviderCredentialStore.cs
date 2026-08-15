using System.Text.Json;
using AzerothPlatform.Infrastructure.Services;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

internal static class CloudProviderCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey);

    internal sealed record AzureCredentials(
        string TenantId,
        string ClientId,
        string ClientSecret,
        string SubscriptionId);

    internal const string OAuthUserType = "oauth_user";

    internal const string AwsAssumedRoleType = "assumed_role";

    internal sealed class AwsAssumedRoleEnvelope
    {
        public string Type { get; set; } = AwsAssumedRoleType;

        public string RoleArn { get; set; } = string.Empty;

        public string ExternalId { get; set; } = string.Empty;
    }

    internal static string ProtectAwsAssumedRole(ISecretProtector protector, AwsAssumedRoleEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.RoleArn) || string.IsNullOrWhiteSpace(envelope.ExternalId))
        {
            throw new ArgumentException("AWS role ARN and External ID are required.");
        }

        envelope.Type = AwsAssumedRoleType;
        envelope.RoleArn = envelope.RoleArn.Trim();
        envelope.ExternalId = envelope.ExternalId.Trim();
        return protector.Protect(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    internal static bool TryParseAwsAssumedRole(string plaintext, out AwsAssumedRoleEnvelope envelope)
    {
        envelope = new AwsAssumedRoleEnvelope();
        if (string.IsNullOrWhiteSpace(plaintext) || plaintext[0] != '{')
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AwsAssumedRoleEnvelope>(plaintext, JsonOptions);
            if (parsed is null
                || !string.Equals(parsed.Type, AwsAssumedRoleType, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(parsed.RoleArn)
                || string.IsNullOrWhiteSpace(parsed.ExternalId))
            {
                return false;
            }

            envelope = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryUnprotectAwsAssumedRole(
        ISecretProtector protector,
        string protectedValue,
        out AwsAssumedRoleEnvelope envelope)
    {
        envelope = new AwsAssumedRoleEnvelope();
        var plaintext = protector.Unprotect(protectedValue).Trim();
        return TryParseAwsAssumedRole(plaintext, out envelope);
    }

    internal sealed class OAuthCredentialEnvelope
    {
        public string Type { get; set; } = OAuthUserType;

        public string AccessToken { get; set; } = string.Empty;

        public string? RefreshToken { get; set; }

        public DateTime? ExpiresAtUtc { get; set; }

        public string? Scope { get; set; }

        public string? Subject { get; set; }
    }

    internal static string ProtectOAuthTokens(ISecretProtector protector, OAuthCredentialEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.AccessToken))
        {
            throw new ArgumentException("OAuth access token is required.");
        }

        envelope.Type = OAuthUserType;
        envelope.AccessToken = envelope.AccessToken.Trim();
        envelope.RefreshToken = string.IsNullOrWhiteSpace(envelope.RefreshToken)
            ? null
            : envelope.RefreshToken.Trim();
        return protector.Protect(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    internal static bool TryUnprotectOAuthTokens(
        ISecretProtector protector,
        string protectedValue,
        out OAuthCredentialEnvelope envelope)
    {
        envelope = new OAuthCredentialEnvelope();
        var plaintext = protector.Unprotect(protectedValue).Trim();
        return TryParseOAuthEnvelope(plaintext, out envelope);
    }

    internal static bool TryParseOAuthEnvelope(string plaintext, out OAuthCredentialEnvelope envelope)
    {
        envelope = new OAuthCredentialEnvelope();
        if (string.IsNullOrWhiteSpace(plaintext) || plaintext[0] != '{')
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<OAuthCredentialEnvelope>(plaintext, JsonOptions);
            if (parsed is null
                || !string.Equals(parsed.Type, OAuthUserType, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(parsed.AccessToken))
            {
                return false;
            }

            envelope = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string ProtectDigitalOceanToken(ISecretProtector protector, string accessToken)
        => ProtectApiToken(protector, accessToken);

    internal static string ProtectApiToken(ISecretProtector protector, string accessToken)
        => protector.Protect(accessToken.Trim());

    internal static string ProtectAwsCredentials(ISecretProtector protector, AwsCredentials credentials)
        => protector.Protect(JsonSerializer.Serialize(credentials, JsonOptions));

    internal static string UnprotectDigitalOceanToken(ISecretProtector protector, string protectedValue)
        => UnprotectApiToken(protector, protectedValue);

    internal static string UnprotectApiToken(ISecretProtector protector, string protectedValue)
    {
        var plaintext = protector.Unprotect(protectedValue).Trim();
        return TryParseOAuthEnvelope(plaintext, out var envelope)
            ? envelope.AccessToken.Trim()
            : plaintext;
    }

    internal static AwsCredentials UnprotectAwsCredentials(ISecretProtector protector, string protectedValue)
    {
        var json = protector.Unprotect(protectedValue);
        if (TryParseAwsAssumedRole(json, out _))
        {
            throw new InvalidOperationException(
                "This AWS connection uses an IAM role. Resolve temporary credentials with the AWS credential resolver.");
        }

        var credentials = JsonSerializer.Deserialize<AwsCredentials>(json, JsonOptions)
                          ?? throw new InvalidOperationException("Stored AWS credentials are invalid.");
        if (string.IsNullOrWhiteSpace(credentials.AccessKeyId)
            || string.IsNullOrWhiteSpace(credentials.SecretAccessKey))
        {
            throw new InvalidOperationException("Stored AWS credentials are incomplete.");
        }

        return credentials;
    }

    internal static string ProtectAzureCredentials(ISecretProtector protector, AzureCredentials credentials)
        => protector.Protect(JsonSerializer.Serialize(credentials, JsonOptions));

    internal static AzureCredentials UnprotectAzureCredentials(ISecretProtector protector, string protectedValue)
    {
        var json = protector.Unprotect(protectedValue);
        var credentials = JsonSerializer.Deserialize<AzureCredentials>(json, JsonOptions)
                          ?? throw new InvalidOperationException("Stored Azure credentials are invalid.");
        if (string.IsNullOrWhiteSpace(credentials.TenantId)
            || string.IsNullOrWhiteSpace(credentials.ClientId)
            || string.IsNullOrWhiteSpace(credentials.ClientSecret)
            || string.IsNullOrWhiteSpace(credentials.SubscriptionId))
        {
            throw new InvalidOperationException("Stored Azure credentials are incomplete.");
        }

        return credentials;
    }

    internal static string ProtectGcpServiceAccountJson(ISecretProtector protector, string serviceAccountJson)
        => protector.Protect(serviceAccountJson.Trim());

    internal static string UnprotectGcpServiceAccountJson(ISecretProtector protector, string protectedValue)
    {
        var json = protector.Unprotect(protectedValue).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Stored GCP credentials could not be decrypted.");
        }

        return json;
    }
}
