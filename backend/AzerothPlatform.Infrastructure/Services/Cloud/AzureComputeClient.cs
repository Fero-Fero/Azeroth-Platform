using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class AzureComputeClient
{
    public async Task ValidateCredentialsAsync(AzureCredentials credentials, CancellationToken cancellationToken)
    {
        var client = CreateArmClient(credentials);
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(credentials.SubscriptionId));
        await subscription.GetAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AzureLocationOption>> ListLocationsAsync(
        AzureCredentials credentials,
        CancellationToken cancellationToken)
    {
        var client = CreateArmClient(credentials);
        var subscription = client.GetDefaultSubscription();
        var locations = new List<AzureLocationOption>();

        await foreach (var location in subscription.GetLocationsAsync().WithCancellation(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(location.Name))
            {
                continue;
            }

            locations.Add(new AzureLocationOption
            {
                Value = location.Name,
                Label = string.IsNullOrWhiteSpace(location.DisplayName)
                    ? location.Name
                    : $"{location.DisplayName} ({location.Name})",
            });
        }

        return locations
            .OrderBy(location => location.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<AzureVmInstance>> ListRunningInstancesAsync(
        AzureCredentials credentials,
        string? locationFilter,
        CancellationToken cancellationToken)
    {
        var client = CreateArmClient(credentials);
        var subscription = client.GetDefaultSubscription();
        var filter = (locationFilter ?? string.Empty).Trim();
        var instances = new List<AzureVmInstance>();

        foreach (var vm in subscription.GetVirtualMachines())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(filter)
                && !string.Equals(vm.Data.Location, filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var instanceView = await vm.InstanceViewAsync();
            var powerState = instanceView.Value.Statuses?
                .FirstOrDefault(status => status.Code?.StartsWith("PowerState/", StringComparison.Ordinal) == true)
                ?.Code;

            if (!string.Equals(powerState, "PowerState/running", StringComparison.Ordinal))
            {
                continue;
            }

            var publicHost = await TryGetPublicIpAsync(client, vm, cancellationToken);
            if (string.IsNullOrWhiteSpace(publicHost))
            {
                continue;
            }

            var imageOffer = vm.Data.StorageProfile?.ImageReference?.Offer ?? string.Empty;
            var imagePublisher = vm.Data.StorageProfile?.ImageReference?.Publisher ?? string.Empty;
            var imageSku = vm.Data.StorageProfile?.ImageReference?.Sku ?? string.Empty;
            var image = string.Join(' ',
                new[] { imagePublisher, imageOffer, imageSku }.Where(part => !string.IsNullOrWhiteSpace(part)));

            instances.Add(new AzureVmInstance
            {
                Id = vm.Id.ToString(),
                Name = vm.Data.Name,
                Location = vm.Data.Location,
                ResourceGroup = vm.Id.ResourceGroupName ?? string.Empty,
                PublicHost = publicHost,
                Image = image,
                SuggestedSshUser = SuggestSshUserFromImage(imageOffer, imagePublisher, imageSku),
            });
        }

        return instances
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task RunBootstrapScriptAsync(
        AzureCredentials credentials,
        string vmResourceId,
        string script,
        CancellationToken cancellationToken)
    {
        var resourceId = (vmResourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new ArgumentException("Azure VM resource id is required.");
        }

        var client = CreateArmClient(credentials);
        var vm = client.GetVirtualMachineResource(new ResourceIdentifier(resourceId));
        var input = new RunCommandInput("RunShellScript")
        {
            Script = { script },
        };

        await vm.RunCommandAsync(WaitUntil.Completed, input, cancellationToken);
    }

    private static ArmClient CreateArmClient(AzureCredentials credentials)
    {
        var tenantId = credentials.TenantId.Trim();
        var clientId = credentials.ClientId.Trim();
        var clientSecret = credentials.ClientSecret.Trim();
        var subscriptionId = credentials.SubscriptionId.Trim();

        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret)
            || string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new InvalidOperationException("Stored Azure credentials are incomplete.");
        }

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        return new ArmClient(credential, subscriptionId);
    }

    private static async Task<string?> TryGetPublicIpAsync(
        ArmClient client,
        VirtualMachineResource vm,
        CancellationToken cancellationToken)
    {
        var networkInterfaces = vm.Data.NetworkProfile?.NetworkInterfaces;
        if (networkInterfaces is null || networkInterfaces.Count == 0)
        {
            return null;
        }

        foreach (var networkInterfaceReference in networkInterfaces)
        {
            if (string.IsNullOrWhiteSpace(networkInterfaceReference.Id))
            {
                continue;
            }

            var nic = client.GetNetworkInterfaceResource(new ResourceIdentifier(networkInterfaceReference.Id!));
            var nicData = await nic.GetAsync(cancellationToken: cancellationToken);
            foreach (var ipConfiguration in nicData.Value.Data.IPConfigurations)
            {
                var publicIpId = ipConfiguration.PublicIPAddress?.Id?.ToString();
                if (string.IsNullOrWhiteSpace(publicIpId))
                {
                    continue;
                }

                var publicIp = client.GetPublicIPAddressResource(new ResourceIdentifier(publicIpId));
                var publicIpData = await publicIp.GetAsync(cancellationToken: cancellationToken);
                var address = publicIpData.Value.Data.IPAddress;
                if (!string.IsNullOrWhiteSpace(address))
                {
                    return address;
                }
            }
        }

        return null;
    }

    private static string SuggestSshUserFromImage(string offer, string publisher, string sku)
    {
        var combined = $"{publisher} {offer} {sku}".ToLowerInvariant();
        if (combined.Contains("ubuntu", StringComparison.Ordinal))
        {
            return "azureuser";
        }

        if (combined.Contains("debian", StringComparison.Ordinal))
        {
            return "debian";
        }

        return "azureuser";
    }

    public sealed class AzureCredentials
    {
        public string TenantId { get; init; } = string.Empty;

        public string ClientId { get; init; } = string.Empty;

        public string ClientSecret { get; init; } = string.Empty;

        public string SubscriptionId { get; init; } = string.Empty;
    }

    public sealed class AzureVmInstance
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Location { get; init; } = string.Empty;

        public string ResourceGroup { get; init; } = string.Empty;

        public string PublicHost { get; init; } = string.Empty;

        public string Image { get; init; } = string.Empty;

        public string SuggestedSshUser { get; init; } = "azureuser";
    }

    public sealed class AzureLocationOption
    {
        public string Value { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;
    }
}
