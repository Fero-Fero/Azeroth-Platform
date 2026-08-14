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
        ICloudAuditService cloudAuditService)
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
                SshUser = "ubuntu",
                SupportsCreate = true,
                SupportsBootstrapExisting = false,
            },
            CloudProvider.Aws => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "us-east-1",
                Size = "t3.medium",
                Image = string.Empty,
                SshUser = "ubuntu",
                SupportsCreate = true,
                SupportsBootstrapExisting = true,
            },
            CloudProvider.Gcp => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "us-central1-a",
                Size = "e2-medium",
                Image = "projects/ubuntu-os-cloud/global/images/family/ubuntu-2204-lts",
                SshUser = "ubuntu",
                SupportsCreate = true,
                SupportsBootstrapExisting = false,
            },
            CloudProvider.Azure => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "eastus",
                Size = string.Empty,
                Image = string.Empty,
                SshUser = "azureuser",
                SupportsCreate = false,
                SupportsBootstrapExisting = true,
            },
            CloudProvider.Hetzner => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "nbg1",
                Size = "cx22",
                Image = "ubuntu-22.04",
                SshUser = "root",
                SupportsCreate = true,
                SupportsBootstrapExisting = false,
            },
            CloudProvider.Vultr => new CloudLaunchDefaultsDto
            {
                Provider = provider,
                Region = defaultRegion ?? "ewr",
                Size = "vc2-2c-4gb",
                Image = string.Empty,
                SshUser = "root",
                SupportsCreate = true,
                SupportsBootstrapExisting = false,
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
        if (request.Mode == CloudLaunchMode.BootstrapExisting)
        {
            throw new ArgumentException("DigitalOcean only supports creating a new droplet from the platform.");
        }

        var accessToken = CloudProviderCredentialStore.UnprotectDigitalOceanToken(
            _secretProtector,
            entity.ProtectedCredentials);

        var name = SanitizeResourceName(request.Name, "azeroth-vpc");
        var region = (request.Region ?? entity.DefaultRegion ?? "nyc3").Trim();
        var size = (request.Size ?? "s-2vcpu-4gb").Trim();
        var image = (request.Image ?? "ubuntu-22-04-x64").Trim();
        var sshUser = SanitizeSshUser(request.SshUser);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser);

        var (savedKeyId, publicKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
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
                },
                SavedSshKeyId = savedKeyId,
                Message = "DigitalOcean droplet created and bootstrap script injected via user data.",
            },
            cancellationToken);
    }

    private async Task<CloudLaunchResultDto> LaunchHetznerAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Mode == CloudLaunchMode.BootstrapExisting)
        {
            throw new ArgumentException("Hetzner Cloud only supports creating a new server from the platform.");
        }

        var accessToken = CloudProviderCredentialStore.UnprotectApiToken(
            _secretProtector,
            entity.ProtectedCredentials);

        var name = SanitizeResourceName(request.Name, "azeroth-vpc");
        var location = (request.Region ?? entity.DefaultRegion ?? "nbg1").Trim();
        var serverType = (request.Size ?? "cx22").Trim();
        var image = (request.Image ?? "ubuntu-22.04").Trim();
        var sshUser = SanitizeSshUser(request.SshUser);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser);

        var (savedKeyId, publicKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
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
                },
                SavedSshKeyId = savedKeyId,
                Message = "Hetzner Cloud server created and bootstrap script injected via user data.",
            },
            cancellationToken);
    }

    private async Task<CloudLaunchResultDto> LaunchVultrAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Mode == CloudLaunchMode.BootstrapExisting)
        {
            throw new ArgumentException("Vultr only supports creating a new instance from the platform.");
        }

        var accessToken = CloudProviderCredentialStore.UnprotectApiToken(
            _secretProtector,
            entity.ProtectedCredentials);

        var label = SanitizeResourceName(request.Name, "azeroth-vpc");
        var region = (request.Region ?? entity.DefaultRegion ?? "ewr").Trim();
        var plan = (request.Size ?? "vc2-2c-4gb").Trim();
        var imageValue = (request.Image ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(imageValue) || !int.TryParse(imageValue, out var osId))
        {
            throw new ArgumentException("Select an operating system for the new Vultr instance.");
        }

        var sshUser = SanitizeSshUser(request.SshUser);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser);

        var (savedKeyId, publicKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
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
            cancellationToken);

        var active = await _vultrClient.WaitForActiveInstanceAsync(
            accessToken,
            created.Id,
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
                },
                SavedSshKeyId = savedKeyId,
                Message = "Vultr instance created and bootstrap script injected via user data.",
            },
            cancellationToken);
    }

    private async Task<CloudLaunchCatalogDto> BuildDigitalOceanCatalogAsync(
        CloudProviderConnectionEntity entity,
        string? regionFilter,
        CancellationToken cancellationToken)
    {
        var accessToken = CloudProviderCredentialStore.UnprotectDigitalOceanToken(
            _secretProtector,
            entity.ProtectedCredentials);

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
        var credentials = CloudProviderCredentialStore.UnprotectAwsCredentials(
            _secretProtector,
            entity.ProtectedCredentials);

        var regions = await _awsEc2Client.ListRegionsAsync(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
            cancellationToken);

        var selectedRegion = (regionFilter ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selectedRegion))
        {
            selectedRegion = entity.DefaultRegion?.Trim() ?? "us-east-1";
        }

        var instanceTypes = await _awsEc2Client.ListLaunchInstanceTypesAsync(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
            selectedRegion,
            cancellationToken);

        var images = await _awsEc2Client.ListLaunchImagesAsync(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
            selectedRegion,
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
        var serviceAccountJson = CloudProviderCredentialStore.UnprotectGcpServiceAccountJson(
            _secretProtector,
            entity.ProtectedCredentials);

        var zones = await _gcpComputeClient.ListZonesAsync(serviceAccountJson, cancellationToken);
        var selectedZone = (zoneFilter ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selectedZone))
        {
            selectedZone = zones.FirstOrDefault()?.Value ?? "us-central1-a";
        }

        var machineTypes = await _gcpComputeClient.ListMachineTypesAsync(
            serviceAccountJson,
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
        var instanceType = (request.Size ?? "t3.medium").Trim();
        var imageId = (request.Image ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(imageId))
        {
            throw new ArgumentException("Select an AMI for the new EC2 instance.");
        }

        var name = SanitizeResourceName(request.Name, "azeroth-vpc");
        var sshUser = SanitizeSshUser(request.SshUser);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser);
        var credentials = CloudProviderCredentialStore.UnprotectAwsCredentials(
            _secretProtector,
            entity.ProtectedCredentials);

        var (savedKeyId, publicKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
        var keyPairName = $"azeroth-{savedKeyId[..8]}";

        var instance = await _awsEc2Client.CreateInstanceAsync(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
            region,
            name,
            instanceType,
            imageId,
            script,
            keyPairName,
            publicKey,
            cancellationToken);

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
                Message = "AWS EC2 instance created and bootstrap script injected via user data.",
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
        var sshUser = SanitizeSshUser(request.SshUser);
        var credentials = CloudProviderCredentialStore.UnprotectAwsCredentials(
            _secretProtector,
            entity.ProtectedCredentials);

        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser);
        var commandId = await _awsSsmClient.SendBootstrapScriptAsync(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
            region,
            instanceId,
            script,
            cancellationToken);

        await _awsSsmClient.WaitForCommandSuccessAsync(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
            region,
            instanceId,
            commandId,
            cancellationToken);

        var instances = await _awsEc2Client.ListRunningInstancesAsync(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
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
        var accessToken = CloudProviderCredentialStore.UnprotectApiToken(
            _secretProtector,
            entity.ProtectedCredentials);

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
        var credentials = CloudProviderCredentialStore.UnprotectAzureCredentials(
            _secretProtector,
            entity.ProtectedCredentials);
        var azureCredentials = CloudProviderConnectionService.ToAzureClientCredentials(credentials);

        var locations = await _azureComputeClient.ListLocationsAsync(azureCredentials, cancellationToken);
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
        var sshUser = SanitizeSshUser(request.SshUser);
        var credentials = CloudProviderCredentialStore.UnprotectAzureCredentials(
            _secretProtector,
            entity.ProtectedCredentials);
        var azureCredentials = CloudProviderConnectionService.ToAzureClientCredentials(credentials);

        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser);
        await _azureComputeClient.RunBootstrapScriptAsync(
            azureCredentials,
            vmResourceId,
            script,
            cancellationToken);

        var instances = await _azureComputeClient.ListRunningInstancesAsync(
            azureCredentials,
            string.IsNullOrWhiteSpace(location) ? null : location,
            cancellationToken);

        var instance = instances.FirstOrDefault(item => item.Id.Equals(vmResourceId, StringComparison.OrdinalIgnoreCase))
                       ?? new AzureComputeClient.AzureVmInstance
                       {
                           Id = vmResourceId,
                           Name = vmResourceId.Split('/').LastOrDefault() ?? vmResourceId,
                           Location = location,
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
                    Provider = CloudProvider.Azure,
                    Name = instance.Name,
                    Region = instance.Location,
                    State = "running",
                    PublicHost = instance.PublicHost,
                    SuggestedSshUser = instance.SuggestedSshUser,
                    Image = instance.Image,
                },
                Message = "Azure Run Command bootstrap completed successfully.",
            },
            cancellationToken);
    }

    private async Task<CloudLaunchResultDto> LaunchGcpAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Mode == CloudLaunchMode.BootstrapExisting)
        {
            throw new ArgumentException("GCP only supports creating a new VM from the platform.");
        }

        var serviceAccountJson = CloudProviderCredentialStore.UnprotectGcpServiceAccountJson(
            _secretProtector,
            entity.ProtectedCredentials);

        var name = SanitizeResourceName(request.Name, "azeroth-vpc");
        var zone = (request.Region ?? entity.DefaultRegion ?? "us-central1-a").Trim();
        var machineType = (request.Size ?? "e2-medium").Trim();
        var sourceImage = (request.Image ?? "projects/ubuntu-os-cloud/global/images/family/ubuntu-2204-lts").Trim();
        var sshUser = SanitizeSshUser(request.SshUser);
        var script = VpcBootstrapUserData.BuildLaunchScript(sshUser);

        var (savedKeyId, publicKey) = await ResolveLaunchSshKeyAsync(request, sshUser, cancellationToken);
        var metadataPublicKey = $"{sshUser}:{publicKey}";

        await _gcpComputeClient.CreateInstanceAsync(
            serviceAccountJson,
            name,
            zone,
            machineType,
            sourceImage,
            script,
            metadataPublicKey,
            cancellationToken);

        var running = await _gcpComputeClient.WaitForRunningInstanceAsync(
            serviceAccountJson,
            zone,
            name,
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
                },
                SavedSshKeyId = savedKeyId,
                Message = "GCP VM created and bootstrap script injected via startup-script metadata.",
            },
            cancellationToken);
    }

    private async Task<(string SavedKeyId, string OpenSshPublicKey)> ResolveLaunchSshKeyAsync(
        CloudLaunchRequestDto request,
        string sshUser,
        CancellationToken cancellationToken)
    {
        var savedKeyId = (request.SavedSshKeyId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(savedKeyId))
        {
            var privateKey = await _cloudSshKeyService.ResolvePrivateKeyAsync(savedKeyId, "launch", cancellationToken);
            return (savedKeyId, SshKeyMaterialHelper.ExtractOpenSshPublicKey(privateKey));
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

        return (created.Id, generated.OpenSshPublicKey);
    }

    private async Task<CloudLaunchResultDto> CompleteLaunchAsync(
        CloudProviderConnectionEntity entity,
        CloudLaunchRequestDto request,
        CloudLaunchResultDto result,
        CancellationToken cancellationToken)
    {
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

    private static string SanitizeSshUser(string sshUser)
        => VpcBootstrapUserData.CreateDto(sshUser).SshUser;
}
