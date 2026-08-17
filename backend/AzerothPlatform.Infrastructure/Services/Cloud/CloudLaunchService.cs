using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Cloud;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AzerothPlatform.Infrastructure.Services;

public sealed class CloudLaunchService : ICloudLaunchService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;
    private readonly ICloudSshKeyService _cloudSshKeyService;
    private readonly DigitalOceanClient _digitalOceanClient;
    private readonly AwsEc2Client _awsEc2Client;
    private readonly AwsSsmClient _awsSsmClient;
    private readonly GcpComputeClient _gcpComputeClient;
    private readonly AzureComputeClient _azureComputeClient;
    private readonly HetznerCloudClient _hetznerCloudClient;
    private readonly VultrClient _vultrClient;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly IAwsCredentialResolver _awsCredentialResolver;
    private readonly IDigitalOceanTokenResolver _digitalOceanTokenResolver;
    private readonly IVultrTokenResolver _vultrTokenResolver;
    private readonly IGcpCredentialResolver _gcpCredentialResolver;
    private readonly IAzureCredentialResolver _azureCredentialResolver;

    public CloudLaunchService(
        AzerothCoreDbContext dbContext,
        ISecretProtector secretProtector,
        ICloudSshKeyService cloudSshKeyService,
        DigitalOceanClient digitalOceanClient,
        AwsEc2Client awsEc2Client,
        AwsSsmClient awsSsmClient,
        GcpComputeClient gcpComputeClient,
        AzureComputeClient azureComputeClient,
        HetznerCloudClient hetznerCloudClient,
        VultrClient vultrClient,
        ICloudAuditService cloudAuditService,
        IAwsCredentialResolver awsCredentialResolver,
        IDigitalOceanTokenResolver digitalOceanTokenResolver,
        IVultrTokenResolver vultrTokenResolver,
        IGcpCredentialResolver gcpCredentialResolver,
        IAzureCredentialResolver azureCredentialResolver)
    {
        _dbContext = dbContext;
        _secretProtector = secretProtector;
        _cloudSshKeyService = cloudSshKeyService;
        _digitalOceanClient = digitalOceanClient;
        _awsEc2Client = awsEc2Client;
        _awsSsmClient = awsSsmClient;
        _gcpComputeClient = gcpComputeClient;
        _azureComputeClient = azureComputeClient;
        _hetznerCloudClient = hetznerCloudClient;
        _vultrClient = vultrClient;
        _cloudAuditService = cloudAuditService;
        _awsCredentialResolver = awsCredentialResolver;
        _digitalOceanTokenResolver = digitalOceanTokenResolver;
        _vultrTokenResolver = vultrTokenResolver;
        _gcpCredentialResolver = gcpCredentialResolver;
        _azureCredentialResolver = azureCredentialResolver;
    }

    public async Task<CloudLaunchDefaultsDto> GetDefaultsAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var entity = await LoadConnectionAsync(connectionId, cancellationToken);
        if (!Enum.TryParse<CloudProvider>(entity.Provider, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException("Unknown cloud provider on this connection.");
        }

        var defaultRegion = string.IsNullOrWhiteSpace(entity.DefaultRegion) ? null : entity.DefaultRegion;
        return provider switch
        {
            CloudProvider.DigitalOcean => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "nyc3",
                Size = "s-2vcpu-4gb",
                Image = "ubuntu-22-04-x64",
                SshUser = VpcBootstrapUserData.DefaultOperatorUser,
                SupportsCreate = true,
                SupportsBootstrapExisting = true,
            },
            CloudProvider.Aws => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "us-east-1",
                Size = "t3.micro",
                Image = string.Empty,
                SshUser = VpcBootstrapUserData.DefaultOperatorUser,
                SupportsCreate = true,
                SupportsBootstrapExisting = true,
            },
            CloudProvider.Gcp => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "us-central1-a",
                Size = "e2-medium",
                Image = "projects/ubuntu-os-cloud/global/images/family/ubuntu-2204-lts",
                SshUser = VpcBootstrapUserData.DefaultOperatorUser,
                SupportsCreate = true,
                SupportsBootstrapExisting = true,
            },
            CloudProvider.Azure => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "eastus",
                Size = string.Empty,
                Image = string.Empty,
                SshUser = VpcBootstrapUserData.DefaultOperatorUser,
                SupportsCreate = false,
                SupportsBootstrapExisting = true,
            },
            CloudProvider.Hetzner => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "nbg1",
                Size = "cx22",
                Image = "ubuntu-22.04",
                SshUser = VpcBootstrapUserData.DefaultOperatorUser,
                SupportsCreate = true,
                SupportsBootstrapExisting = true,
            },
            CloudProvider.Vultr => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "ewr",
                Size = "vc2-2c-4gb",
                Image = string.Empty,
                SshUser = VpcBootstrapUserData.DefaultOperatorUser,
                SupportsCreate = true,
                SupportsBootstrapExisting = true,
            },
            _ => throw new InvalidOperationException($"{provider} launch defaults are not supported yet."),
        };
    }

    public async Task<CloudLaunchCatalogDto> GetCatalogAsync(
        string connectionId,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        var entity = await LoadConnectionAsync(connectionId, cancellationToken);
        if (!Enum.TryParse<CloudProvider>(entity.Provider, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException("Unknown cloud provider on this connection.");
        }

        var regionFilter = string.IsNullOrWhiteSpace(region) ? entity.DefaultRegion : region.Trim();

        return provider switch
        {
            CloudProvider.DigitalOcean => await BuildDigitalOceanCatalogAsync(entity, regionFilter, cancellationToken),
            CloudProvider.Aws => await BuildAwsCatalogAsync(entity, regionFilter, cancellationToken),
            CloudProvider.Gcp => await BuildGcpCatalogAsync(entity, regionFilter, cancellationToken),
            CloudProvider.Azure => await BuildAzureCatalogAsync(entity, regionFilter, cancellationToken),
            CloudProvider.Hetzner => await BuildHetznerCatalogAsync(entity, regionFilter, cancellationToken),
            CloudProvider.Vultr => await BuildVultrCatalogAsync(entity, regionFilter, cancellationToken),
            _ => throw new InvalidOperationException($"{provider} launch catalog is not supported yet."),
        };
    }

    public async Task<CloudLaunchResultDto> LaunchAsync(
        string connectionId,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var entity = await LoadConnectionAsync(connectionId, cancellationToken);
        if (!Enum.TryParse<CloudProvider>(entity.Provider, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException("Unknown cloud provider on this connection.");
        }

        return provider switch
        {
            CloudProvider.DigitalOcean => await LaunchDigitalOceanAsync(entity, request, cancellationToken),
            CloudProvider.Aws => await LaunchAwsAsync(entity, request, cancellationToken),
            CloudProvider.Gcp => await LaunchGcpAsync(entity, request, cancellationToken),
            CloudProvider.Azure => await LaunchAzureAsync(entity, request, cancellationToken),
            CloudProvider.Hetzner => await LaunchHetznerAsync(entity, request, cancellationToken),
            CloudProvider.Vultr => await LaunchVultrAsync(entity, request, cancellationToken),
            _ => throw new InvalidOperationException($"{provider} launch is not supported yet."),
        };
    }

    private async Task<CloudLaunchResultDto> LaunchDigitalOceanAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        var accessToken = await _digitalOceanTokenResolver.ResolveAsync(entity, cancellationToken);

        if (request.Mode == CloudLaunchMode.BootstrapExisting)
        {
            var existing = await _digitalOceanClient.FindDropletAsync(
                accessToken,
                request.InstanceId,
                publicHost: null,
                cancellationToken)
                ?? throw new ArgumentException("DigitalOcean droplet id is required to apply Cloud Firewall on an existing VM.");

            var existingFirewall = await _digitalOceanClient.ApplyDropletFirewallAsync(
                accessToken,
                $"azeroth-platform-{existing.Id}",
                existing.Id,
                ToDigitalOceanInbound(LaunchInboundRules(request)),
                cancellationToken);
            var existingPublicIp = existing.Networks?.V4
                .FirstOrDefault(network => string.Equals(network.Type, "public", StringComparison.OrdinalIgnoreCase))
                ?.IpAddress ?? string.Empty;
            var existingImageSlug = existing.Image?.Slug ?? string.Empty;
            return await CompleteLaunchAsync(
                entity,
                request,
                new CloudLaunchResultDto
                {
                    Instance = new CloudInstanceDto
                    {
                        Id = existing.Id.ToString(),
                        Provider = CloudProvider.DigitalOcean,
                        Name = existing.Name,
                        Region = existing.Region?.Slug ?? string.Empty,
                        State = existing.Status,
                        PublicHost = existingPublicIp,
                        SuggestedSshUser = CloudProviderConnectionService.SuggestSshUserFromImage(
                            existingImageSlug,
                            existing.Image?.Distribution),
                        Image = string.IsNullOrWhiteSpace(existingImageSlug)
                            ? existing.Image?.Distribution ?? string.Empty
                            : existingImageSlug,
                        InstanceType = existing.SizeSlug,
                    },
                    Message = request.ApplyNetworkProfile
                        ? $"DigitalOcean Cloud Firewall {existingFirewall.Name} now allows SSH, game, and web ports on this droplet."
                        : $"DigitalOcean Cloud Firewall {existingFirewall.Name} allows SSH.",
                },
                cancellationToken);
        }

        var name = SanitizeResourceName(request.Name, "azeroth-vpc");
        var region = (request.Region ?? entity.DefaultRegion ?? "nyc3").Trim();
        var size = (request.Size ?? "s-2vcpu-4gb").Trim();
        var image = (request.Image ?? "ubuntu-22-04-x64").Trim();
        var sshUser = VpcBootstrapUserData.EnsureLaunchSshUser(request.SshUser);
        var (savedKeyId, publicKey, generatedPrivateKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser, publicKey);
        var doKeyId = await _digitalOceanClient.UploadAccountSshKeyAsync(
            accessToken,
            $"azeroth-{savedKeyId[..8]}",
            publicKey,
            cancellationToken);

        var droplet = await _digitalOceanClient.CreateDropletAsync(
            accessToken,
            name,
            region,
            size,
            image,
            script,
            [doKeyId],
            cancellationToken);

        var active = await _digitalOceanClient.WaitForActiveDropletAsync(
            accessToken,
            droplet.Id,
            cancellationToken);

        var publicIp = active.Networks?.V4
            .FirstOrDefault(network => string.Equals(network.Type, "public", StringComparison.OrdinalIgnoreCase))
            ?.IpAddress ?? string.Empty;

        var firewall = await _digitalOceanClient.ApplyDropletFirewallAsync(
            accessToken,
            $"azeroth-platform-{active.Id}",
            active.Id,
            ToDigitalOceanInbound(request.ApplyNetworkProfile
                ? VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr)
                :
                [
                    new VpcSecurityRuleDto
                    {
                        Port = 22,
                        Source = string.IsNullOrWhiteSpace(request.AdminSourceCidr)
                            ? "0.0.0.0/0"
                            : request.AdminSourceCidr.Trim(),
                        Description = "SSH for platform bootstrap",
                    },
                ]),
            cancellationToken);

        var imageSlug = active.Image?.Slug ?? string.Empty;
        return await CompleteLaunchAsync(
            entity,
            request,
            new CloudLaunchResultDto
            {
                Instance = new CloudInstanceDto
                {
                    Id = active.Id.ToString(),
                    Provider = CloudProvider.DigitalOcean,
                    Name = active.Name,
                    Region = active.Region?.Slug ?? region,
                    State = active.Status,
                    PublicHost = publicIp,
                    SuggestedSshUser = CloudProviderConnectionService.SuggestSshUserFromImage(
                        imageSlug,
                        active.Image?.Distribution),
                    Image = string.IsNullOrWhiteSpace(imageSlug) ? image : imageSlug,
                    InstanceType = string.IsNullOrWhiteSpace(active.SizeSlug) ? size : active.SizeSlug,
                },
                SavedSshKeyId = savedKeyId,
                PrivateKeyPem = generatedPrivateKey,
                Message = request.ApplyNetworkProfile
                    ? $"DigitalOcean droplet created. User data installs Docker, ufw, and OS baselines; Cloud Firewall {firewall.Name} allows SSH, game, and web ports."
                    : "DigitalOcean droplet created and bootstrap script injected via user data.",
            },
            cancellationToken);
    }

    private async Task<CloudLaunchResultDto> LaunchHetznerAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        var accessToken = CloudProviderCredentialStore.UnprotectApiToken(
            _secretProtector,
            entity.ProtectedCredentials);

        if (request.Mode == CloudLaunchMode.BootstrapExisting)
        {
            var server = await _hetznerCloudClient.FindServerAsync(
                accessToken,
                request.InstanceId,
                publicHost: null,
                cancellationToken)
                ?? throw new ArgumentException("Hetzner server id is required to apply Cloud Firewall on an existing VM.");

            var inbound = ToHetznerInbound(request.ApplyNetworkProfile
                ? VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr)
                :
                [
                    new VpcSecurityRuleDto
                    {
                        Port = 22,
                        Source = string.IsNullOrWhiteSpace(request.AdminSourceCidr)
                            ? "0.0.0.0/0"
                            : request.AdminSourceCidr.Trim(),
                        Description = "SSH for platform bootstrap",
                    },
                ]);
            var firewall = await _hetznerCloudClient.ApplyFirewallAsync(
                accessToken,
                $"azeroth-platform-{server.Id}",
                server.Id,
                inbound,
                cancellationToken);
            var existingImageName = server.Image?.Name ?? string.Empty;
            return await CompleteLaunchAsync(
                entity,
                request,
                new CloudLaunchResultDto
                {
                    Instance = new CloudInstanceDto
                    {
                        Id = server.Id.ToString(),
                        Provider = CloudProvider.Hetzner,
                        Name = server.Name,
                        Region = server.Datacenter?.Location?.Name ?? server.Datacenter?.Name ?? string.Empty,
                        State = server.Status,
                        PublicHost = server.PublicIpv4,
                        SuggestedSshUser = HetznerCloudClient.SuggestSshUserFromImage(existingImageName),
                        Image = existingImageName,
                        InstanceType = server.ServerType,
                    },
                    Message = request.ApplyNetworkProfile
                        ? $"Hetzner Cloud Firewall {firewall.Name} now allows SSH, game, and web ports on this server."
                        : $"Hetzner Cloud Firewall {firewall.Name} allows SSH.",
                },
                cancellationToken);
        }

        var name = SanitizeResourceName(request.Name, "azeroth-vpc");
        var location = (request.Region ?? entity.DefaultRegion ?? "nbg1").Trim();
        var serverType = (request.Size ?? "cx22").Trim();
        var image = (request.Image ?? "ubuntu-22.04").Trim();
        var sshUser = VpcBootstrapUserData.EnsureLaunchSshUser(request.SshUser);
        var (savedKeyId, publicKey, generatedPrivateKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser, publicKey);
        var sshKeyId = await _hetznerCloudClient.UploadSshKeyAsync(
            accessToken,
            $"azeroth-{savedKeyId[..8]}",
            publicKey,
            cancellationToken);

        var created = await _hetznerCloudClient.CreateServerAsync(
            accessToken,
            name,
            location,
            serverType,
            image,
            script,
            [sshKeyId],
            cancellationToken);

        var active = await _hetznerCloudClient.WaitForRunningServerAsync(
            accessToken,
            created.Id,
            cancellationToken);

        var firewallName = $"azeroth-platform-{active.Id}";
        var launchInbound = ToHetznerInbound(request.ApplyNetworkProfile
            ? VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr)
            :
            [
                new VpcSecurityRuleDto
                {
                    Port = 22,
                    Source = string.IsNullOrWhiteSpace(request.AdminSourceCidr)
                        ? "0.0.0.0/0"
                        : request.AdminSourceCidr.Trim(),
                    Description = "SSH for platform bootstrap",
                },
            ]);
        var applied = await _hetznerCloudClient.ApplyFirewallAsync(
            accessToken,
            firewallName,
            active.Id,
            launchInbound,
            cancellationToken);

        var imageName = active.Image?.Name ?? image;
        return await CompleteLaunchAsync(
            entity,
            request,
            new CloudLaunchResultDto
            {
                Instance = new CloudInstanceDto
                {
                    Id = active.Id.ToString(),
                    Provider = CloudProvider.Hetzner,
                    Name = active.Name,
                    Region = active.Datacenter?.Location?.Name ?? location,
                    State = active.Status,
                    PublicHost = active.PublicIpv4,
                    SuggestedSshUser = HetznerCloudClient.SuggestSshUserFromImage(imageName),
                    Image = imageName,
                    InstanceType = string.IsNullOrWhiteSpace(active.ServerType) ? serverType : active.ServerType,
                },
                SavedSshKeyId = savedKeyId,
                PrivateKeyPem = generatedPrivateKey,
                Message = request.ApplyNetworkProfile
                    ? $"Hetzner Cloud server created. User data installs Docker, ufw, and OS baselines; Cloud Firewall {applied.Name} allows SSH, game, and web ports."
                    : "Hetzner Cloud server created and bootstrap script injected via user data.",
            },
            cancellationToken);
    }

    private async Task<CloudLaunchResultDto> LaunchVultrAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        var accessToken = await _vultrTokenResolver.ResolveAsync(entity, cancellationToken);

        if (request.Mode == CloudLaunchMode.BootstrapExisting)
        {
            var instance = await _vultrClient.FindInstanceAsync(
                accessToken,
                request.InstanceId,
                publicHost: null,
                cancellationToken)
                ?? throw new ArgumentException("Vultr instance id is required to apply a firewall group on an existing VM.");

            var existingFirewall = await _vultrClient.ApplyFirewallGroupAsync(
                accessToken,
                $"azeroth-platform-{instance.Id}",
                instance.Id,
                ToVultrInbound(LaunchInboundRules(request)),
                cancellationToken);
            return await CompleteLaunchAsync(
                entity,
                request,
                new CloudLaunchResultDto
                {
                    Instance = new CloudInstanceDto
                    {
                        Id = instance.Id,
                        Provider = CloudProvider.Vultr,
                        Name = instance.Label,
                        Region = instance.Region,
                        State = instance.Status,
                        PublicHost = instance.PublicHost,
                        SuggestedSshUser = instance.SuggestedSshUser,
                        Image = instance.Os,
                        InstanceType = instance.Plan,
                    },
                    Message = request.ApplyNetworkProfile
                        ? $"Vultr firewall group {existingFirewall.Description} now allows SSH, game, and web ports on this instance."
                        : $"Vultr firewall group {existingFirewall.Description} allows SSH.",
                },
                cancellationToken);
        }

        var label = SanitizeResourceName(request.Name, "azeroth-vpc");
        var region = (request.Region ?? entity.DefaultRegion ?? "ewr").Trim();
        var plan = (request.Size ?? "vc2-2c-4gb").Trim();
        var imageValue = (request.Image ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(imageValue) || !int.TryParse(imageValue, out var osId))
        {
            throw new ArgumentException("Select an operating system for the new Vultr instance.");
        }

        var sshUser = VpcBootstrapUserData.EnsureLaunchSshUser(request.SshUser);
        var (savedKeyId, publicKey, generatedPrivateKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser, publicKey);
        var sshKeyId = await _vultrClient.UploadSshKeyAsync(
            accessToken,
            $"azeroth-{savedKeyId[..8]}",
            publicKey,
            cancellationToken);

        var created = await _vultrClient.CreateInstanceAsync(
            accessToken,
            label,
            region,
            plan,
            osId,
            script,
            [sshKeyId],
            firewallGroupId: null,
            cancellationToken);

        var active = await _vultrClient.WaitForActiveInstanceAsync(
            accessToken,
            created.Id,
            cancellationToken);

        var firewall = await _vultrClient.ApplyFirewallGroupAsync(
            accessToken,
            $"azeroth-platform-{active.Id}",
            active.Id,
            ToVultrInbound(request.ApplyNetworkProfile
                ? VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr)
                :
                [
                    new VpcSecurityRuleDto
                    {
                        Port = 22,
                        Source = string.IsNullOrWhiteSpace(request.AdminSourceCidr)
                            ? "0.0.0.0/0"
                            : request.AdminSourceCidr.Trim(),
                        Description = "SSH for platform bootstrap",
                    },
                ]),
            cancellationToken);

        return await CompleteLaunchAsync(
            entity,
            request,
            new CloudLaunchResultDto
            {
                Instance = new CloudInstanceDto
                {
                    Id = active.Id,
                    Provider = CloudProvider.Vultr,
                    Name = active.Label,
                    Region = active.Region,
                    State = active.Status,
                    PublicHost = active.PublicHost,
                    SuggestedSshUser = active.SuggestedSshUser,
                    Image = active.Os,
                    InstanceType = string.IsNullOrWhiteSpace(active.Plan) ? plan : active.Plan,
                },
                SavedSshKeyId = savedKeyId,
                PrivateKeyPem = generatedPrivateKey,
                Message = request.ApplyNetworkProfile
                    ? $"Vultr instance created. User data installs Docker, ufw, and OS baselines; firewall group {firewall.Description} allows SSH, game, and web ports."
                    : "Vultr instance created and bootstrap script injected via user data.",
            },
            cancellationToken);
    }

    private async Task<CloudLaunchCatalogDto> BuildDigitalOceanCatalogAsync(
        CloudProviderConnectionEntity entity,
        string? regionFilter,
        CancellationToken cancellationToken)
    {
        var accessToken = await _digitalOceanTokenResolver.ResolveAsync(entity, cancellationToken);
        var regionsTask = _digitalOceanClient.ListRegionsAsync(accessToken, cancellationToken);
        var sizesTask = _digitalOceanClient.ListSizesAsync(accessToken, regionFilter, cancellationToken);
        var imagesTask = _digitalOceanClient.ListDistributionImagesAsync(accessToken, cancellationToken);
        await Task.WhenAll(regionsTask, sizesTask, imagesTask);

        var regions = await regionsTask;
        var sizes = await sizesTask;
        var images = await imagesTask;

        return new CloudLaunchCatalogDto
        {
            Provider = CloudProvider.DigitalOcean,
            Regions = regions
                .Select(region => new CloudLaunchCatalogOptionDto
                {
                    Value = region.Slug,
                    Label = $"{region.Name} ({region.Slug})",
                })
                .ToList(),
            Sizes = sizes
                .Select(size => new CloudLaunchCatalogOptionDto
                {
                    Value = size.Slug,
                    Label = $"{size.Slug} ({size.Vcpus} vCPU, {size.Memory / 1024} GB RAM, {size.Disk} GB disk)",
                })
                .ToList(),
            Images = images
                .Select(image => new CloudLaunchCatalogOptionDto
                {
                    Value = image.Slug,
                    Label = $"{image.Distribution} {image.Name}",
                    Description = image.Slug,
                })
                .ToList(),
        };
    }

    private async Task<CloudLaunchCatalogDto> BuildAwsCatalogAsync(
        CloudProviderConnectionEntity entity,
        string? regionFilter,
        CancellationToken cancellationToken)
    {
        var credentials = await _awsCredentialResolver.ResolveAsync(entity, cancellationToken);

        var regions = await _awsEc2Client.ListRegionsAsync(
            credentials,
            cancellationToken);

        var selectedRegion = (regionFilter ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selectedRegion))
        {
            selectedRegion = entity.DefaultRegion?.Trim() ?? "us-east-1";
        }

        var instanceTypes = await _awsEc2Client.ListLaunchInstanceTypesAsync(
            credentials,
            selectedRegion,
            cancellationToken);

        var architectures = instanceTypes
            .Select(instanceType => instanceType.Description)
            .Where(architecture => !string.IsNullOrWhiteSpace(architecture))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var images = await _awsEc2Client.ListLaunchImagesAsync(
            credentials,
            selectedRegion,
            architectures,
            cancellationToken);

        return new CloudLaunchCatalogDto
        {
            Provider = CloudProvider.Aws,
            Regions = regions
                .Select(region => new CloudLaunchCatalogOptionDto
                {
                    Value = region,
                    Label = region,
                })
                .ToList(),
            Sizes = instanceTypes
                .Select(instanceType => new CloudLaunchCatalogOptionDto
                {
                    Value = instanceType.Value,
                    Label = instanceType.Label,
                    Description = instanceType.Description,
                })
                .ToList(),
            Images = images
                .Select(image => new CloudLaunchCatalogOptionDto
                {
                    Value = image.Value,
                    Label = image.Label,
                    Description = image.Description,
                })
                .ToList(),
        };
    }

    private async Task<CloudLaunchCatalogDto> BuildGcpCatalogAsync(
        CloudProviderConnectionEntity entity,
        string? zoneFilter,
        CancellationToken cancellationToken)
    {
        var access = await _gcpCredentialResolver.ResolveAsync(entity, cancellationToken);
        var zones = await _gcpComputeClient.ListZonesAsync(access, cancellationToken);
        var selectedZone = (zoneFilter ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selectedZone))
        {
            selectedZone = zones.FirstOrDefault()?.Value ?? "us-central1-a";
        }

        var machineTypes = await _gcpComputeClient.ListMachineTypesAsync(
            access,
            selectedZone,
            cancellationToken);

        return new CloudLaunchCatalogDto
        {
            Provider = CloudProvider.Gcp,
            Regions = zones
                .Select(zone => new CloudLaunchCatalogOptionDto
                {
                    Value = zone.Value,
                    Label = zone.Label,
                    Description = zone.Description,
                })
                .ToList(),
            Sizes = machineTypes
                .Select(machineType => new CloudLaunchCatalogOptionDto
                {
                    Value = machineType.Value,
                    Label = machineType.Label,
                    Description = machineType.Description,
                })
                .ToList(),
            Images = _gcpComputeClient.ListLaunchImages()
                .Select(image => new CloudLaunchCatalogOptionDto
                {
                    Value = image.Value,
                    Label = image.Label,
                    Description = image.Description,
                })
                .ToList(),
        };
    }

    private async Task<CloudLaunchResultDto> LaunchAwsAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
        => request.Mode == CloudLaunchMode.BootstrapExisting
            ? await BootstrapAwsAsync(entity, request, cancellationToken)
            : await LaunchAwsCreateAsync(entity, request, cancellationToken);

    private async Task<CloudLaunchResultDto> LaunchAwsCreateAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        var region = (request.Region ?? entity.DefaultRegion ?? "us-east-1").Trim();
        var instanceType = (request.Size ?? "t3.micro").Trim();
        var imageId = (request.Image ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(imageId))
        {
            throw new ArgumentException("Select an AMI for the new EC2 instance.");
        }

        var name = SanitizeResourceName(request.Name, "azeroth-vpc");
        var sshUser = VpcBootstrapUserData.EnsureLaunchSshUser(request.SshUser);
        var credentials = await _awsCredentialResolver.ResolveAsync(entity, cancellationToken);

        var (savedKeyId, publicKey, generatedPrivateKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser, publicKey);
        var keyPairName = $"azeroth-{savedKeyId[..8]}";

        var instance = await _awsEc2Client.CreateInstanceAsync(
            credentials,
            region,
            name,
            instanceType,
            imageId,
            script,
            keyPairName,
            publicKey,
            cancellationToken,
            request.AdminSourceCidr,
            request.ApplyNetworkProfile);

        return await CompleteLaunchAsync(
            entity,
            request,
            new CloudLaunchResultDto
            {
                Instance = new CloudInstanceDto
                {
                    Id = instance.Id,
                    Provider = CloudProvider.Aws,
                    Name = instance.Name,
                    Region = instance.Region,
                    State = instance.State,
                    PublicHost = instance.PublicHost,
                    SuggestedSshUser = instance.SuggestedSshUser,
                    Image = instance.Image,
                },
                SavedSshKeyId = savedKeyId,
                PrivateKeyPem = generatedPrivateKey,
                Message = request.ApplyNetworkProfile
                    ? "AWS EC2 instance created. User data installs Docker, ufw, and OS baselines; the security group allows SSH, game, and web ports."
                    : "AWS EC2 instance created and bootstrap script injected via user data.",
            },
            cancellationToken);
    }

    private async Task<CloudLaunchResultDto> BootstrapAwsAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        var instanceId = (request.InstanceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new ArgumentException("AWS instance id is required for SSM bootstrap.");
        }

        var region = (request.Region ?? entity.DefaultRegion ?? "us-east-1").Trim();
        var sshUser = VpcBootstrapUserData.EnsureLaunchSshUser(request.SshUser);
        var credentials = await _awsCredentialResolver.ResolveAsync(entity, cancellationToken);

        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser);
        var commandId = await _awsSsmClient.SendBootstrapScriptAsync(
            credentials,
            region,
            instanceId,
            script,
            cancellationToken);

        await _awsSsmClient.WaitForCommandSuccessAsync(
            credentials,
            region,
            instanceId,
            commandId,
            cancellationToken);

        var instances = await _awsEc2Client.ListRunningInstancesAsync(
            credentials,
            region,
            cancellationToken);

        var instance = instances.FirstOrDefault(item => item.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                         ?? new AwsEc2Client.AwsEc2Instance
                         {
                             Id = instanceId,
                             Name = instanceId,
                             Region = region,
                             State = "running",
                             PublicHost = string.Empty,
                             SuggestedSshUser = sshUser,
                         };

        return await CompleteLaunchAsync(
            entity,
            request,
            new CloudLaunchResultDto
            {
                Instance = new CloudInstanceDto
                {
                    Id = instance.Id,
                    Provider = CloudProvider.Aws,
                    Name = instance.Name,
                    Region = instance.Region,
                    State = instance.State,
                    PublicHost = instance.PublicHost,
                    SuggestedSshUser = instance.SuggestedSshUser,
                    Image = instance.Image,
                },
                Message = "AWS SSM bootstrap completed successfully.",
                BootstrapCommandId = commandId,
            },
            cancellationToken);
    }

    private async Task<CloudLaunchCatalogDto> BuildHetznerCatalogAsync(
        CloudProviderConnectionEntity entity,
        string? locationFilter,
        CancellationToken cancellationToken)
    {
        var accessToken = CloudProviderCredentialStore.UnprotectApiToken(
            _secretProtector,
            entity.ProtectedCredentials);

        var locationsTask = _hetznerCloudClient.ListLocationsAsync(accessToken, cancellationToken);
        var imagesTask = _hetznerCloudClient.ListImagesAsync(accessToken, cancellationToken);
        await Task.WhenAll(locationsTask, imagesTask);

        var locations = await locationsTask;
        var images = await imagesTask;
        var selectedLocation = (locationFilter ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selectedLocation))
        {
            selectedLocation = entity.DefaultRegion?.Trim() ?? "nbg1";
        }

        var serverTypes = await _hetznerCloudClient.ListServerTypesAsync(
            accessToken,
            selectedLocation,
            cancellationToken);

        return new CloudLaunchCatalogDto
        {
            Provider = CloudProvider.Hetzner,
            Regions = locations
                .Select(location => new CloudLaunchCatalogOptionDto
                {
                    Value = location.Name,
                    Label = $"{location.City}, {location.Country} ({location.Name})",
                })
                .ToList(),
            Sizes = serverTypes
                .Select(serverType => new CloudLaunchCatalogOptionDto
                {
                    Value = serverType.Name,
                    Label = $"{serverType.Name} ({serverType.Cores} vCPU, {serverType.Memory:0} GB RAM, {serverType.Disk} GB disk)",
                })
                .ToList(),
            Images = images
                .Select(image => new CloudLaunchCatalogOptionDto
                {
                    Value = image.Name,
                    Label = image.Description,
                    Description = image.Name,
                })
                .ToList(),
        };
    }

    private async Task<CloudLaunchCatalogDto> BuildVultrCatalogAsync(
        CloudProviderConnectionEntity entity,
        string? regionFilter,
        CancellationToken cancellationToken)
    {
        var accessToken = await _vultrTokenResolver.ResolveAsync(entity, cancellationToken);
        var regionsTask = _vultrClient.ListRegionsAsync(accessToken, cancellationToken);
        var osTask = _vultrClient.ListOperatingSystemsAsync(accessToken, cancellationToken);
        await Task.WhenAll(regionsTask, osTask);

        var regions = await regionsTask;
        var operatingSystems = await osTask;
        var selectedRegion = (regionFilter ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selectedRegion))
        {
            selectedRegion = entity.DefaultRegion?.Trim() ?? "ewr";
        }

        var plans = await _vultrClient.ListPlansAsync(accessToken, selectedRegion, cancellationToken);

        return new CloudLaunchCatalogDto
        {
            Provider = CloudProvider.Vultr,
            Regions = regions
                .Select(region => new CloudLaunchCatalogOptionDto
                {
                    Value = region.Id,
                    Label = $"{region.City}, {region.Country} ({region.Id})",
                })
                .ToList(),
            Sizes = plans
                .Select(plan => new CloudLaunchCatalogOptionDto
                {
                    Value = plan.Id,
                    Label = $"{plan.Id} ({plan.VcpuCount} vCPU, {plan.Ram / 1024} GB RAM, {plan.Disk} GB disk)",
                })
                .ToList(),
            Images = operatingSystems
                .Select(os => new CloudLaunchCatalogOptionDto
                {
                    Value = os.Id.ToString(),
                    Label = os.Name,
                    Description = os.Id.ToString(),
                })
                .ToList(),
        };
    }

    private async Task<CloudLaunchCatalogDto> BuildAzureCatalogAsync(
        CloudProviderConnectionEntity entity,
        string? locationFilter,
        CancellationToken cancellationToken)
    {
        var access = await _azureCredentialResolver.ResolveAsync(entity, cancellationToken);
        var locations = await _azureComputeClient.ListLocationsAsync(access, cancellationToken);
        var selectedLocation = (locationFilter ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selectedLocation))
        {
            selectedLocation = entity.DefaultRegion?.Trim() ?? "eastus";
        }

        return new CloudLaunchCatalogDto
        {
            Provider = CloudProvider.Azure,
            Regions = locations
                .Select(location => new CloudLaunchCatalogOptionDto
                {
                    Value = location.Value,
                    Label = location.Label,
                })
                .ToList(),
            Sizes = [],
            Images = [],
        };
    }

    private async Task<CloudLaunchResultDto> LaunchAzureAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Mode == CloudLaunchMode.Create)
        {
            throw new ArgumentException("Azure only supports bootstrapping an existing VM via Run Command.");
        }

        var vmResourceId = (request.InstanceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(vmResourceId))
        {
            throw new ArgumentException("Azure VM resource id is required for Run Command bootstrap.");
        }

        var location = (request.Region ?? entity.DefaultRegion ?? string.Empty).Trim();
        var sshUser = VpcBootstrapUserData.EnsureLaunchSshUser(request.SshUser);
        var access = await _azureCredentialResolver.ResolveAsync(entity, cancellationToken);

        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser);
        await _azureComputeClient.RunBootstrapScriptAsync(
            access,
            vmResourceId,
            script,
            cancellationToken);

        var instance = await _azureComputeClient.FindInstanceAsync(
            access,
            vmResourceId,
            publicHost: null,
            cancellationToken)
            ?? new AzureComputeClient.AzureVmInstance
            {
                Id = vmResourceId,
                Name = vmResourceId.Split('/').LastOrDefault() ?? vmResourceId,
                Location = location,
                PublicHost = string.Empty,
                SuggestedSshUser = sshUser,
            };

        var nsgMessage = string.Empty;
        var inbound = ToAzureInbound(request.ApplyNetworkProfile
            ? VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr)
            :
            [
                new VpcSecurityRuleDto
                {
                    Port = 22,
                    Source = string.IsNullOrWhiteSpace(request.AdminSourceCidr)
                        ? "0.0.0.0/0"
                        : request.AdminSourceCidr.Trim(),
                    Description = "SSH for platform bootstrap",
                },
            ]);
        var (applied, _) = await _azureComputeClient.ApplyNsgRulesAsync(
            access,
            instance.Id,
            inbound,
            cancellationToken);
        nsgMessage = request.ApplyNetworkProfile
            ? $" NSG updated with {applied} inbound rule(s) (SSH, game, and web ports)."
            : $" NSG allows SSH ({applied} rule(s)).";

        return await CompleteLaunchAsync(
            entity,
            request,
            new CloudLaunchResultDto
            {
                Instance = new CloudInstanceDto
                {
                    Id = instance.Id,
                    Provider = CloudProvider.Azure,
                    Name = instance.Name,
                    Region = instance.Location,
                    State = "running",
                    PublicHost = instance.PublicHost,
                    SuggestedSshUser = instance.SuggestedSshUser,
                    Image = instance.Image,
                    InstanceType = instance.VmSize,
                },
                Message = "Azure Run Command bootstrap completed successfully." + nsgMessage,
            },
            cancellationToken);
    }

    private async Task<CloudLaunchResultDto> LaunchGcpAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        var access = await _gcpCredentialResolver.ResolveAsync(entity, cancellationToken);

        if (request.Mode == CloudLaunchMode.BootstrapExisting)
        {
            var instance = await _gcpComputeClient.FindInstanceAsync(
                access,
                request.InstanceId,
                publicHost: null,
                cancellationToken)
                ?? throw new ArgumentException("GCP instance id is required to apply VPC firewall rules on an existing VM.");

            var applied = await _gcpComputeClient.ApplyFirewallRulesAsync(
                access,
                instance.Name,
                instance,
                ToGcpInbound(LaunchInboundRules(request)),
                cancellationToken);
            return await CompleteLaunchAsync(
                entity,
                request,
                new CloudLaunchResultDto
                {
                    Instance = new CloudInstanceDto
                    {
                        Id = instance.Id,
                        Provider = CloudProvider.Gcp,
                        Name = instance.Name,
                        Region = instance.Zone,
                        State = instance.State,
                        PublicHost = instance.PublicHost,
                        SuggestedSshUser = instance.SuggestedSshUser,
                        Image = instance.Image,
                        InstanceType = instance.MachineType,
                    },
                    Message = request.ApplyNetworkProfile
                        ? $"GCP VPC firewall updated ({applied} ingress rule(s)) targeting tag {GcpComputeClient.PlatformNetworkTag}."
                        : $"GCP VPC firewall allows SSH ({applied} ingress rule(s)).",
                },
                cancellationToken);
        }

        var name = SanitizeResourceName(request.Name, "azeroth-vpc");
        var zone = (request.Region ?? entity.DefaultRegion ?? "us-central1-a").Trim();
        var machineType = (request.Size ?? "e2-medium").Trim();
        var sourceImage = (request.Image ?? "projects/ubuntu-os-cloud/global/images/family/ubuntu-2204-lts").Trim();
        var sshUser = VpcBootstrapUserData.EnsureLaunchSshUser(request.SshUser);
        var (savedKeyId, publicKey, generatedPrivateKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser, publicKey);
        var metadataPublicKey = $"{sshUser}:{publicKey}";

        await _gcpComputeClient.CreateInstanceAsync(
            access,
            name,
            zone,
            machineType,
            sourceImage,
            script,
            metadataPublicKey,
            cancellationToken);

        var running = await _gcpComputeClient.WaitForRunningInstanceAsync(
            access,
            zone,
            name,
            cancellationToken);

        var firewallRules = ToGcpInbound(request.ApplyNetworkProfile
            ? VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr)
            :
            [
                new VpcSecurityRuleDto
                {
                    Port = 22,
                    Source = string.IsNullOrWhiteSpace(request.AdminSourceCidr)
                        ? "0.0.0.0/0"
                        : request.AdminSourceCidr.Trim(),
                    Description = "SSH for platform bootstrap",
                },
            ]);
        await _gcpComputeClient.ApplyFirewallRulesAsync(
            access,
            running.Name,
            running,
            firewallRules,
            cancellationToken);

        return await CompleteLaunchAsync(
            entity,
            request,
            new CloudLaunchResultDto
            {
                Instance = new CloudInstanceDto
                {
                    Id = running.Id,
                    Provider = CloudProvider.Gcp,
                    Name = running.Name,
                    Region = running.Zone,
                    State = running.State,
                    PublicHost = running.PublicHost,
                    SuggestedSshUser = running.SuggestedSshUser,
                    Image = running.Image,
                    InstanceType = string.IsNullOrWhiteSpace(running.MachineType) ? machineType : running.MachineType,
                },
                SavedSshKeyId = savedKeyId,
                PrivateKeyPem = generatedPrivateKey,
                Message = request.ApplyNetworkProfile
                    ? "GCP VM created. Startup-script installs Docker, ufw, and OS baselines; VPC firewall rules targeting tag azeroth-platform allow SSH, game, and web ports."
                    : "GCP VM created and bootstrap script injected via startup-script metadata.",
            },
            cancellationToken);
    }

    private async Task<(string SavedKeyId, string OpenSshPublicKey, string? GeneratedPrivateKeyPem)> ResolveLaunchSshKeyAsync(
        CloudLaunchRequestDto request,
        string sshUser,
        CancellationToken cancellationToken)
    {
        var savedKeyId = (request.SavedSshKeyId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(savedKeyId))
        {
            var privateKey = await _cloudSshKeyService.ResolvePrivateKeyAsync(savedKeyId, "launch", cancellationToken);
            return (savedKeyId, SshKeyMaterialHelper.ExtractOpenSshPublicKey(privateKey), null);
        }

        if (!request.GenerateSshKey)
        {
            throw new ArgumentException("Select a saved SSH key or allow the platform to generate one.");
        }

        var generated = SshKeyMaterialHelper.GenerateKeyPair();
        var created = await _cloudSshKeyService.CreateAsync(
            new CreateCloudSshKeyRequestDto
            {
                Label = $"Launch key {generated.Fingerprint}",
                PrivateKey = generated.PrivateKeyPem,
                DefaultSshUser = sshUser,
            },
            cancellationToken);

        return (created.Id, generated.OpenSshPublicKey, generated.PrivateKeyPem);
    }

    private async Task<CloudLaunchResultDto> CompleteLaunchAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CloudLaunchResultDto result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.Instance.InstanceType))
        {
            result.Instance.InstanceType = (request.Size ?? string.Empty).Trim();
        }

        result.Instance.SuggestedSshUser = VpcBootstrapUserData.EnsureLaunchSshUser(request.SshUser);

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.LaunchCompleted,
                ResourceType = "launch",
                ResourceId = result.Instance.Id,
                Summary = result.Message,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    connectionId = entity.Id,
                    provider = entity.Provider,
                    mode = request.Mode.ToString(),
                    instanceName = result.Instance.Name,
                    region = result.Instance.Region,
                    publicHost = result.Instance.PublicHost,
                    savedSshKeyId = result.SavedSshKeyId,
                    bootstrapCommandId = result.BootstrapCommandId,
                }),
            },
            cancellationToken);

        return result;
    }

    private async Task<CloudProviderConnectionEntity> LoadConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken)
        => await _dbContext.CloudProviderConnections.AsNoTracking()
               .FirstOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken)
           ?? throw new KeyNotFoundException("Cloud connection not found.");

    private static string SanitizeResourceName(string value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return $"{fallback}-{Guid.NewGuid():N}"[..32];
        }

        var sanitized = new string(trimmed
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-')
            .ToArray())
            .Trim('-');

        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized[..Math.Min(sanitized.Length, 63)];
    }

    private static IReadOnlyList<VpcSecurityRuleDto> LaunchInboundRules(CloudLaunchRequestDto request)
        => request.ApplyNetworkProfile
            ? VpcSecurityCatalog.BuildLaunchCloudIngressRules(request.AdminSourceCidr)
            :
            [
                new VpcSecurityRuleDto
                {
                    Port = 22,
                    Source = string.IsNullOrWhiteSpace(request.AdminSourceCidr)
                        ? "0.0.0.0/0"
                        : request.AdminSourceCidr.Trim(),
                    Description = "SSH for platform bootstrap",
                },
            ];

    private static List<DigitalOceanClient.DigitalOceanFirewallInboundRule> ToDigitalOceanInbound(
        IEnumerable<VpcSecurityRuleDto> rules)
        => rules
            .Select(rule => new DigitalOceanClient.DigitalOceanFirewallInboundRule
            {
                Protocol = "tcp",
                Ports = rule.Port.ToString(),
                SourceAddresses =
                [
                    string.IsNullOrWhiteSpace(rule.Source) ? "0.0.0.0/0" : rule.Source.Trim(),
                ],
            })
            .ToList();

    private static List<GcpComputeClient.GcpFirewallInboundRule> ToGcpInbound(
        IEnumerable<VpcSecurityRuleDto> rules)
        => rules
            .Select(rule => new GcpComputeClient.GcpFirewallInboundRule
            {
                Port = rule.Port,
                SourceCidr = string.IsNullOrWhiteSpace(rule.Source) ? "0.0.0.0/0" : rule.Source.Trim(),
                Description = rule.Description,
            })
            .ToList();

    private static List<AzureComputeClient.AzureNsgInboundRule> ToAzureInbound(
        IEnumerable<VpcSecurityRuleDto> rules)
        => rules
            .Select(rule => new AzureComputeClient.AzureNsgInboundRule
            {
                Port = rule.Port,
                SourceCidr = string.IsNullOrWhiteSpace(rule.Source) ? "0.0.0.0/0" : rule.Source.Trim(),
                Description = rule.Description,
            })
            .ToList();

    private static List<VultrClient.VultrFirewallInboundRule> ToVultrInbound(
        IEnumerable<VpcSecurityRuleDto> rules)
        => rules
            .Select(rule =>
            {
                var cidr = string.IsNullOrWhiteSpace(rule.Source) ? "0.0.0.0/0" : rule.Source.Trim();
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

    private static List<HetznerCloudClient.HetznerFirewallInboundRule> ToHetznerInbound(
        IEnumerable<VpcSecurityRuleDto> rules)
        => rules
            .Select(rule => new HetznerCloudClient.HetznerFirewallInboundRule
            {
                Port = rule.Port.ToString(),
                SourceIps =
                [
                    string.IsNullOrWhiteSpace(rule.Source) ? "0.0.0.0/0" : rule.Source.Trim(),
                ],
                Description = rule.Description,
            })
            .ToList();
}
