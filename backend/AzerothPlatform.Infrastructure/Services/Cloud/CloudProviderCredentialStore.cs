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

    internal static string ProtectDigitalOceanToken(ISecretProtector protector, string accessToken)
        => ProtectApiToken(protector, accessToken);

    internal static string ProtectApiToken(ISecretProtector protector, string accessToken)
        => protector.Protect(accessToken.Trim());

    internal static string ProtectAwsCredentials(ISecretProtector protector, AwsCredentials credentials)
        => protector.Protect(JsonSerializer.Serialize(credentials, JsonOptions));

    internal static string UnprotectDigitalOceanToken(ISecretProtector protector, string protectedValue)
        => UnprotectApiToken(protector, protectedValue);

    internal static string UnprotectApiToken(ISecretProtector protector, string protectedValue)
        => protector.Unprotect(protectedValue).Trim();

    internal static AwsCredentials UnprotectAwsCredentials(ISecretProtector protector, string protectedValue)
    {
        var json = protector.Unprotect(protectedValue);
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
