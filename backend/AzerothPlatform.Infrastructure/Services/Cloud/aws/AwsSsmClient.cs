using Amazon;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class AwsSsmClient
{
    public async Task<string> SendBootstrapScriptAsync(
        AwsRuntimeCredentials credentials,
        string region,
        string instanceId,
        string script,
        CancellationToken cancellationToken,
        bool powershell = false)
    {
        using var client = CreateClient(credentials, region);
        var response = await client.SendCommandAsync(new SendCommandRequest
        {
            DocumentName = powershell ? "AWS-RunPowerShellScript" : "AWS-RunShellScript",
            InstanceIds = [instanceId],
            Parameters = new Dictionary<string, List<string>>
            {
                ["commands"] = [script],
            },
            Comment = "Azeroth Platform VPC bootstrap",
        }, cancellationToken);

        return response.Command?.CommandId
               ?? throw new InvalidOperationException("AWS SSM did not return a command id.");
    }

    public async Task WaitForCommandSuccessAsync(
        AwsRuntimeCredentials credentials,
        string region,
        string instanceId,
        string commandId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials, region);
        const int maxAttempts = 60;

        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await client.ListCommandInvocationsAsync(new ListCommandInvocationsRequest
            {
                CommandId = commandId,
                InstanceId = instanceId,
                Details = true,
            }, cancellationToken);

            var invocation = response.CommandInvocations.FirstOrDefault();
            if (invocation is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            switch (invocation.Status.Value)
            {
                case "Success":
                    return;
                case "Failed":
                case "Cancelled":
                case "TimedOut":
                    throw new InvalidOperationException(
                        $"AWS SSM bootstrap failed with status {invocation.Status.Value}.");
                default:
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    break;
            }
        }

        throw new InvalidOperationException("Timed out waiting for AWS SSM bootstrap to complete.");
    }

    private static AmazonSimpleSystemsManagementClient CreateClient(
        AwsRuntimeCredentials credentials,
        string region)
    {
        var config = new AmazonSimpleSystemsManagementConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
        };
        return new AmazonSimpleSystemsManagementClient(credentials.ToSdk(), config);
    }
}
