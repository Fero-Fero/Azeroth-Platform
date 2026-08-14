using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Compute.V1;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class GcpComputeClient
{
    private const string ComputeReadOnlyScope = "https://www.googleapis.com/auth/compute.readonly";
    private const string ComputeScope = "https://www.googleapis.com/auth/compute";

    public Task ValidateServiceAccountJsonAsync(string serviceAccountJson, CancellationToken cancellationToken)
        => ValidateAndBuildClientsAsync(serviceAccountJson, readWrite: false, cancellationToken);

    public async Task<GcpComputeInstance> CreateInstanceAsync(
        string serviceAccountJson,
        string name,
        string zone,
        string machineType,
        string sourceImage,
        string startupScript,
        string? sshPublicKey,
        CancellationToken cancellationToken)
    {
        var json = (serviceAccountJson ?? string.Empty).Trim();
        var projectId = ExtractProjectId(json);
        var credential = GoogleCredential.FromJson(json).CreateScoped(ComputeScope);
        var instancesClient = await new InstancesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);

        var instance = new Instance
        {
            Name = name,
            MachineType = $"zones/{zone}/machineTypes/{machineType}",
            Disks =
            {
                new AttachedDisk
                {
                    Boot = true,
                    AutoDelete = true,
                    InitializeParams = new AttachedDiskInitializeParams
                    {
                        SourceImage = sourceImage,
                    },
                },
            },
            NetworkInterfaces =
            {
                new NetworkInterface
                {
                    AccessConfigs =
                    {
                        new AccessConfig
                        {
                            Name = "External NAT",
                            Type = "ONE_TO_ONE_NAT",
                        },
                    },
                },
            },
            Metadata = new Metadata
            {
                Items =
                {
                    new Items { Key = "startup-script", Value = startupScript },
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(sshPublicKey))
        {
            instance.Metadata.Items.Add(new Items
            {
                Key = "ssh-keys",
                Value = sshPublicKey,
            });
        }

        var operation = await instancesClient.InsertAsync(projectId, zone, instance, cancellationToken);
        await operation.PollUntilCompletedAsync();

        var created = await instancesClient.GetAsync(projectId, zone, name, cancellationToken);
        var publicHost = ResolvePublicHost(created);
        var imageHint = BuildImageHint(created);

        return new GcpComputeInstance
        {
            Id = $"{zone}/{created.Name}",
            Name = created.Name,
            Zone = zone,
            State = created.Status,
            PublicHost = publicHost,
            Image = imageHint,
            MachineType = ParseResourceName(created.MachineType),
            SuggestedSshUser = SuggestSshUser(imageHint),
        };
    }

    public async Task<GcpComputeInstance> WaitForRunningInstanceAsync(
        string serviceAccountJson,
        string zone,
        string name,
        CancellationToken cancellationToken)
    {
        var json = (serviceAccountJson ?? string.Empty).Trim();
        var projectId = ExtractProjectId(json);
        var credential = GoogleCredential.FromJson(json).CreateScoped(ComputeReadOnlyScope);
        var instancesClient = await new InstancesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);

        const int maxAttempts = 60;
        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await instancesClient.GetAsync(projectId, zone, name, cancellationToken);
            var publicHost = ResolvePublicHost(instance);
            if (string.Equals(instance.Status, "RUNNING", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(publicHost))
            {
                var imageHint = BuildImageHint(instance);
                return new GcpComputeInstance
                {
                    Id = $"{zone}/{instance.Name}",
                    Name = instance.Name,
                    Zone = zone,
                    State = instance.Status,
                    PublicHost = publicHost,
                    Image = imageHint,
                    MachineType = ParseResourceName(instance.MachineType),
                    SuggestedSshUser = SuggestSshUser(imageHint),
                };
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException("Timed out waiting for the GCP instance to become running.");
    }

    public async Task<IReadOnlyList<GcpComputeInstance>> ListRunningInstancesAsync(
        string serviceAccountJson,
        string? zoneFilter,
        CancellationToken cancellationToken)
    {
        var (projectId, instancesClient) = await ValidateAndBuildClientsAsync(serviceAccountJson, readWrite: false, cancellationToken);
        var filter = (zoneFilter ?? string.Empty).Trim();
        var instances = new List<GcpComputeInstance>();

        await foreach (var scopedList in instancesClient
                           .AggregatedListAsync(new AggregatedListInstancesRequest { Project = projectId })
                           .WithCancellation(cancellationToken))
        {
            if (scopedList.Value?.Instances == null || scopedList.Value.Instances.Count == 0)
            {
                continue;
            }

            var zone = ParseZoneName(scopedList.Key);
            if (!MatchesZoneFilter(zone, filter))
            {
                continue;
            }

            foreach (var instance in scopedList.Value.Instances)
            {
                if (!string.Equals(instance.Status, "RUNNING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var publicHost = ResolvePublicHost(instance);
                if (string.IsNullOrWhiteSpace(publicHost))
                {
                    continue;
                }

                var imageHint = BuildImageHint(instance);
                instances.Add(new GcpComputeInstance
                {
                    Id = $"{zone}/{instance.Name}",
                    Name = instance.Name,
                    Zone = zone,
                    State = instance.Status,
                    PublicHost = publicHost,
                    Image = imageHint,
                    MachineType = ParseResourceName(instance.MachineType),
                    SuggestedSshUser = SuggestSshUser(imageHint),
                });
            }
        }

        return instances
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Zone, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GcpCatalogOption>> ListZonesAsync(
        string serviceAccountJson,
        CancellationToken cancellationToken)
    {
        var json = (serviceAccountJson ?? string.Empty).Trim();
        var projectId = ExtractProjectId(json);
        var credential = GoogleCredential.FromJson(json).CreateScoped(ComputeReadOnlyScope);
        var zonesClient = await new ZonesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);

        var zones = new List<GcpCatalogOption>();
        await foreach (var zone in zonesClient.ListAsync(projectId).WithCancellation(cancellationToken))
        {
            if (!string.Equals(zone.Status, "UP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            zones.Add(new GcpCatalogOption
            {
                Value = zone.Name,
                Label = zone.Name,
                Description = zone.Description,
            });
        }

        return zones
            .OrderBy(zone => zone.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GcpCatalogOption>> ListMachineTypesAsync(
        string serviceAccountJson,
        string zone,
        CancellationToken cancellationToken)
    {
        var zoneName = (zone ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(zoneName))
        {
            throw new ArgumentException("GCP zone is required to list machine types.");
        }

        var json = (serviceAccountJson ?? string.Empty).Trim();
        var projectId = ExtractProjectId(json);
        var credential = GoogleCredential.FromJson(json).CreateScoped(ComputeReadOnlyScope);
        var machineTypesClient = await new MachineTypesClientBuilder { Credential = credential }
            .BuildAsync(cancellationToken);

        var machineTypes = new List<GcpCatalogOption>();
        await foreach (var machineType in machineTypesClient.ListAsync(projectId, zoneName).WithCancellation(cancellationToken))
        {
            var memoryGb = machineType.MemoryMb / 1024m;
            machineTypes.Add(new GcpCatalogOption
            {
                Value = machineType.Name,
                Label = $"{machineType.Name} ({machineType.GuestCpus} vCPU, {memoryGb:0.#} GB RAM)",
                Description = machineType.Description,
            });
        }

        return machineTypes
            .OrderBy(machineType => machineType.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<GcpCatalogOption> ListLaunchImages()
        =>
        [
            new GcpCatalogOption
            {
                Value = "projects/ubuntu-os-cloud/global/images/family/ubuntu-2204-lts",
                Label = "Ubuntu 22.04 LTS",
                Description = "Recommended for AzerothPlatform VPC stacks.",
            },
            new GcpCatalogOption
            {
                Value = "projects/ubuntu-os-cloud/global/images/family/ubuntu-2404-lts",
                Label = "Ubuntu 24.04 LTS",
            },
            new GcpCatalogOption
            {
                Value = "projects/debian-cloud/global/images/family/debian-12",
                Label = "Debian 12",
            },
        ];

    private static async Task<(string ProjectId, InstancesClient InstancesClient)> ValidateAndBuildClientsAsync(
        string serviceAccountJson,
        bool readWrite,
        CancellationToken cancellationToken)
    {
        var json = (serviceAccountJson ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("GCP service account JSON is required.");
        }

        var projectId = ExtractProjectId(json);
        var scope = readWrite ? ComputeScope : ComputeReadOnlyScope;
        var credential = GoogleCredential.FromJson(json).CreateScoped(scope);

        var zonesClient = await new ZonesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);
        try
        {
            await zonesClient.ListAsync(new ListZonesRequest
            {
                Project = projectId,
                MaxResults = 1,
            }).ReadPageAsync(1, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ParseGcpError(ex, "GCP rejected the service account credentials."));
        }

        var instancesClient = await new InstancesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);
        return (projectId, instancesClient);
    }

    internal static string ExtractProjectId(string serviceAccountJson)
    {
        try
        {
            using var document = JsonDocument.Parse(serviceAccountJson);
            if (document.RootElement.TryGetProperty("project_id", out var projectIdElement))
            {
                var projectId = projectIdElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(projectId))
                {
                    return projectId;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"GCP service account JSON is invalid: {ex.Message}");
        }

        throw new ArgumentException("GCP service account JSON must include project_id.");
    }

    internal static bool MatchesZoneFilter(string zone, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return zone.Equals(filter, StringComparison.OrdinalIgnoreCase)
               || zone.StartsWith($"{filter}-", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ParseZoneName(string scopedListKey)
    {
        if (string.IsNullOrWhiteSpace(scopedListKey))
        {
            return string.Empty;
        }

        const string zonesPrefix = "zones/";
        if (scopedListKey.StartsWith(zonesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return scopedListKey[zonesPrefix.Length..];
        }

        return scopedListKey;
    }

    internal static string ParseResourceName(string resourceUrl)
    {
        if (string.IsNullOrWhiteSpace(resourceUrl))
        {
            return string.Empty;
        }

        var lastSlash = resourceUrl.LastIndexOf('/');
        return lastSlash >= 0 ? resourceUrl[(lastSlash + 1)..] : resourceUrl;
    }

    internal static string ResolvePublicHost(Instance instance)
    {
        foreach (var networkInterface in instance.NetworkInterfaces)
        {
            foreach (var accessConfig in networkInterface.AccessConfigs)
            {
                if (!string.IsNullOrWhiteSpace(accessConfig.NatIP))
                {
                    return accessConfig.NatIP;
                }
            }
        }

        return string.Empty;
    }

    internal static string BuildImageHint(Instance instance)
    {
        var parts = new List<string>();

        var machineType = ParseResourceName(instance.MachineType);
        if (!string.IsNullOrWhiteSpace(machineType))
        {
            parts.Add(machineType);
        }

        foreach (var disk in instance.Disks)
        {
            foreach (var license in disk.Licenses)
            {
                if (!string.IsNullOrWhiteSpace(license))
                {
                    parts.Add(ParseResourceName(license));
                }
            }

            if (!string.IsNullOrWhiteSpace(disk.Source))
            {
                parts.Add(ParseResourceName(disk.Source));
            }
        }

        return string.Join(" / ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    internal static string SuggestSshUser(string imageHint)
    {
        var combined = (imageHint ?? string.Empty).ToLowerInvariant();
        if (combined.Contains("ubuntu", StringComparison.Ordinal))
        {
            return "ubuntu";
        }

        if (combined.Contains("debian", StringComparison.Ordinal))
        {
            return "debian";
        }

        if (combined.Contains("centos", StringComparison.Ordinal) || combined.Contains("rhel", StringComparison.Ordinal))
        {
            return "centos";
        }

        return "ubuntu";
    }

    private static string ParseGcpError(Exception exception, string fallback)
    {
        return exception.Message switch
        {
            { Length: > 0 } message when message.Length <= 400 => message,
            { Length: > 400 } message => message[..400] + "…",
            _ => fallback,
        };
    }

    public sealed class GcpComputeInstance
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Zone { get; init; } = string.Empty;

        public string State { get; init; } = string.Empty;

        public string PublicHost { get; init; } = string.Empty;

        public string Image { get; init; } = string.Empty;

        public string MachineType { get; init; } = string.Empty;

        public string SuggestedSshUser { get; init; } = "ubuntu";
    }

    public sealed class GcpCatalogOption
    {
        public string Value { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string? Description { get; init; }
    }
}
