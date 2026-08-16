using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Compute.V1;
using Grpc.Core;
using ComputeMetadata = Google.Cloud.Compute.V1.Metadata;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class GcpComputeClient
{
    public const string PlatformNetworkTag = "azeroth-platform";

    public const string OAuthAuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";

    public const string OAuthTokenUrl = "https://oauth2.googleapis.com/token";

    public const string OAuthRevokeUrl = "https://oauth2.googleapis.com/revoke";

    public const string OAuthTokenInfoUrl = "https://oauth2.googleapis.com/tokeninfo";

    public const string OAuthScopes =
        "https://www.googleapis.com/auth/compute https://www.googleapis.com/auth/cloudplatformprojects.readonly";

    private const string ComputeReadOnlyScope = "https://www.googleapis.com/auth/compute.readonly";
    private const string ComputeScope = "https://www.googleapis.com/auth/compute";
    private const string ProjectsListUrl = "https://cloudresourcemanager.googleapis.com/v1/projects";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public GcpComputeClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public static GcpAccess FromServiceAccountJson(string serviceAccountJson, string? projectId = null)
    {
        var json = (serviceAccountJson ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("GCP service account JSON is required.");
        }

        var resolvedProjectId = string.IsNullOrWhiteSpace(projectId)
            ? ExtractProjectId(json)
            : projectId.Trim();
        return new GcpAccess
        {
            Credential = GoogleCredential.FromJson(json).CreateScoped(ComputeScope),
            ProjectId = resolvedProjectId,
        };
    }

    public static GcpAccess FromAccessToken(string accessToken, string? projectId = null)
    {
        var token = (accessToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("GCP access token is required.");
        }

        return new GcpAccess
        {
            Credential = GoogleCredential.FromAccessToken(token),
            ProjectId = (projectId ?? string.Empty).Trim(),
            AccessToken = token,
        };
    }

    public Task ValidateServiceAccountJsonAsync(string serviceAccountJson, CancellationToken cancellationToken)
        => ValidateAccessAsync(FromServiceAccountJson(serviceAccountJson), cancellationToken);

    public async Task ValidateAccessAsync(GcpAccess access, CancellationToken cancellationToken)
    {
        RequireProjectId(access);
        var credential = access.Credential.CreateScoped(ComputeReadOnlyScope);
        var zonesClient = await new ZonesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);
        try
        {
            await zonesClient.ListAsync(new ListZonesRequest
            {
                Project = access.ProjectId,
                MaxResults = 1,
            }).ReadPageAsync(1, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ParseGcpError(
                ex,
                "GCP rejected the credentials or Compute Engine API is not enabled on this project."));
        }
    }

    public async Task<GcpComputeInstance> CreateInstanceAsync(
        GcpAccess access,
        string name,
        string zone,
        string machineType,
        string sourceImage,
        string startupScript,
        string? sshPublicKey,
        CancellationToken cancellationToken)
    {
        RequireProjectId(access);
        var credential = access.Credential.CreateScoped(ComputeScope);
        var instancesClient = await new InstancesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);

        var instance = new Instance
        {
            Name = name,
            MachineType = $"zones/{zone}/machineTypes/{machineType}",
            Tags = new Tags { Items = { PlatformNetworkTag } },
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
            Metadata = new ComputeMetadata
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

        var operation = await instancesClient.InsertAsync(access.ProjectId, zone, instance, cancellationToken);
        await operation.PollUntilCompletedAsync();

        var created = await instancesClient.GetAsync(access.ProjectId, zone, name, cancellationToken);
        return ToComputeInstance(created, zone);
    }

    public async Task<GcpComputeInstance> WaitForRunningInstanceAsync(
        GcpAccess access,
        string zone,
        string name,
        CancellationToken cancellationToken)
    {
        RequireProjectId(access);
        var credential = access.Credential.CreateScoped(ComputeReadOnlyScope);
        var instancesClient = await new InstancesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);

        const int maxAttempts = 60;
        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await instancesClient.GetAsync(access.ProjectId, zone, name, cancellationToken);
            var publicHost = ResolvePublicHost(instance);
            if (string.Equals(instance.Status, "RUNNING", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(publicHost))
            {
                return ToComputeInstance(instance, zone);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException("Timed out waiting for the GCP instance to become running.");
    }

    public async Task<IReadOnlyList<GcpComputeInstance>> ListRunningInstancesAsync(
        GcpAccess access,
        string? zoneFilter,
        CancellationToken cancellationToken)
    {
        RequireProjectId(access);
        var credential = access.Credential.CreateScoped(ComputeReadOnlyScope);
        var instancesClient = await new InstancesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);
        var filter = (zoneFilter ?? string.Empty).Trim();
        var instances = new List<GcpComputeInstance>();

        await foreach (var scopedList in instancesClient
                           .AggregatedListAsync(new AggregatedListInstancesRequest { Project = access.ProjectId })
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

                instances.Add(ToComputeInstance(instance, zone));
            }
        }

        return instances
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Zone, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GcpCatalogOption>> ListZonesAsync(
        GcpAccess access,
        CancellationToken cancellationToken)
    {
        RequireProjectId(access);
        var credential = access.Credential.CreateScoped(ComputeReadOnlyScope);
        var zonesClient = await new ZonesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);

        var zones = new List<GcpCatalogOption>();
        await foreach (var zone in zonesClient.ListAsync(access.ProjectId).WithCancellation(cancellationToken))
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
        GcpAccess access,
        string zone,
        CancellationToken cancellationToken)
    {
        var zoneName = (zone ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(zoneName))
        {
            throw new ArgumentException("GCP zone is required to list machine types.");
        }

        RequireProjectId(access);
        var credential = access.Credential.CreateScoped(ComputeReadOnlyScope);
        var machineTypesClient = await new MachineTypesClientBuilder { Credential = credential }
            .BuildAsync(cancellationToken);

        var machineTypes = new List<GcpCatalogOption>();
        await foreach (var machineType in machineTypesClient.ListAsync(access.ProjectId, zoneName)
                           .WithCancellation(cancellationToken))
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

    public async Task<IReadOnlyList<GcpCatalogOption>> ListProjectsAsync(
        GcpAccess access,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(access.AccessToken) && !string.IsNullOrWhiteSpace(access.ProjectId))
        {
            return
            [
                new GcpCatalogOption
                {
                    Value = access.ProjectId,
                    Label = access.ProjectId,
                    Description = "Service account project",
                },
            ];
        }

        var token = await ResolveBearerTokenAsync(access, cancellationToken);
        var projects = new List<GcpCatalogOption>();
        string? pageToken = null;

        do
        {
            var url = $"{ProjectsListUrl}?filter=lifecycleState:ACTIVE&pageSize=200";
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ParseHttpError(
                    body,
                    "Could not list Google Cloud projects. Enable the Cloud Resource Manager API and grant cloudplatformprojects.readonly."));
            }

            var payload = JsonSerializer.Deserialize<GcpProjectListResponse>(body, JsonOptions)
                          ?? new GcpProjectListResponse();
            foreach (var project in payload.Projects)
            {
                var projectId = (project.ProjectId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    continue;
                }

                var name = (project.Name ?? string.Empty).Trim();
                projects.Add(new GcpCatalogOption
                {
                    Value = projectId,
                    Label = string.IsNullOrWhiteSpace(name) || string.Equals(name, projectId, StringComparison.Ordinal)
                        ? projectId
                        : $"{name} ({projectId})",
                    Description = projectId,
                });
            }

            pageToken = payload.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return projects
            .OrderBy(project => project.Label, StringComparer.OrdinalIgnoreCase)
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

    public async Task<GcpComputeInstance?> FindInstanceAsync(
        GcpAccess access,
        string? instanceId,
        string? publicHost,
        CancellationToken cancellationToken)
    {
        RequireProjectId(access);
        var id = (instanceId ?? string.Empty).Trim();
        var host = (publicHost ?? string.Empty).Trim();
        if (TryParseInstanceId(id, out var zone, out var name))
        {
            try
            {
                var credential = access.Credential.CreateScoped(ComputeReadOnlyScope);
                var instancesClient = await new InstancesClientBuilder { Credential = credential }
                    .BuildAsync(cancellationToken);
                var instance = await instancesClient.GetAsync(access.ProjectId, zone, name, cancellationToken);
                return ToComputeInstance(instance, zone);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                // Fall through to public-IP search.
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var running = await ListRunningInstancesAsync(access, zoneFilter: null, cancellationToken);
        return running.FirstOrDefault(instance =>
            string.Equals(instance.PublicHost, host, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<int> ApplyFirewallRulesAsync(
        GcpAccess access,
        string resourceId,
        GcpComputeInstance instance,
        IReadOnlyList<GcpFirewallInboundRule> rules,
        CancellationToken cancellationToken)
    {
        RequireProjectId(access);
        var credential = access.Credential.CreateScoped(ComputeScope);
        var instancesClient = await new InstancesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);
        var live = await instancesClient.GetAsync(access.ProjectId, instance.Zone, instance.Name, cancellationToken);
        await EnsurePlatformTagAsync(instancesClient, access.ProjectId, instance.Zone, live, cancellationToken);

        var network = live.NetworkInterfaces.FirstOrDefault()?.Network;
        if (string.IsNullOrWhiteSpace(network))
        {
            throw new InvalidOperationException("GCP instance has no VPC network to attach firewall rules to.");
        }

        var firewallsClient = await new FirewallsClientBuilder { Credential = credential }.BuildAsync(cancellationToken);
        var applied = 0;
        foreach (var rule in rules)
        {
            var name = BuildFirewallName(resourceId, rule.Port);
            var firewall = new Firewall
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(rule.Description)
                    ? $"Azeroth Platform tcp/{rule.Port}"
                    : rule.Description.Trim(),
                Network = network,
                Direction = "INGRESS",
                Priority = 1000,
                Allowed =
                {
                    new Allowed
                    {
                        IPProtocol = "tcp",
                        Ports = { rule.Port.ToString() },
                    },
                },
                SourceRanges = { rule.SourceCidr },
                TargetTags = { PlatformNetworkTag },
            };

            try
            {
                var existing = await firewallsClient.GetAsync(access.ProjectId, name, cancellationToken);
                existing.Description = firewall.Description;
                existing.Network = network;
                existing.Direction = "INGRESS";
                existing.Allowed.Clear();
                existing.Allowed.Add(firewall.Allowed[0]);
                existing.SourceRanges.Clear();
                existing.SourceRanges.Add(rule.SourceCidr);
                existing.TargetTags.Clear();
                existing.TargetTags.Add(PlatformNetworkTag);
                var patch = await firewallsClient.PatchAsync(access.ProjectId, name, existing, cancellationToken);
                await patch.PollUntilCompletedAsync();
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                var insert = await firewallsClient.InsertAsync(access.ProjectId, firewall, cancellationToken);
                await insert.PollUntilCompletedAsync();
            }

            applied += 1;
        }

        return applied;
    }

    public async Task<IReadOnlyList<GcpFirewallProbeRule>> ListInstanceFirewallRulesAsync(
        GcpAccess access,
        GcpComputeInstance instance,
        CancellationToken cancellationToken)
    {
        RequireProjectId(access);
        var credential = access.Credential.CreateScoped(ComputeReadOnlyScope);
        var instancesClient = await new InstancesClientBuilder { Credential = credential }.BuildAsync(cancellationToken);
        var live = await instancesClient.GetAsync(access.ProjectId, instance.Zone, instance.Name, cancellationToken);
        var tags = live.Tags?.Items?.ToList() ?? [];
        var firewallsClient = await new FirewallsClientBuilder { Credential = credential }.BuildAsync(cancellationToken);
        var rules = new List<GcpFirewallProbeRule>();

        await foreach (var firewall in firewallsClient.ListAsync(access.ProjectId).WithCancellation(cancellationToken))
        {
            if (!string.Equals(firewall.Direction, "INGRESS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetTags = firewall.TargetTags.ToList();
            if (targetTags.Count > 0
                && !targetTags.Contains(PlatformNetworkTag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var allowed in firewall.Allowed)
            {
                rules.Add(new GcpFirewallProbeRule
                {
                    Name = firewall.Name,
                    Protocol = allowed.IPProtocol ?? "tcp",
                    Ports = allowed.Ports.ToList(),
                    SourceRanges = firewall.SourceRanges.ToList(),
                    TargetTags = targetTags,
                    InstanceHasPlatformTag = tags.Contains(PlatformNetworkTag, StringComparer.OrdinalIgnoreCase),
                });
            }
        }

        if (rules.Count == 0)
        {
            rules.Add(new GcpFirewallProbeRule
            {
                InstanceHasPlatformTag = tags.Contains(PlatformNetworkTag, StringComparer.OrdinalIgnoreCase),
            });
        }
        else
        {
            var hasTag = tags.Contains(PlatformNetworkTag, StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                rule.InstanceHasPlatformTag = hasTag;
            }
        }

        return rules;
    }

    public async Task<GcpOAuthToken> ExchangeAuthorizationCodeAsync(
        string clientId,
        string clientSecret,
        string redirectUri,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code.Trim(),
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
            ["redirect_uri"] = redirectUri.Trim(),
            ["code_verifier"] = codeVerifier.Trim(),
        };
        return await PostOAuthTokenAsync(form, cancellationToken);
    }

    public async Task<GcpOAuthToken> RefreshAccessTokenAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken.Trim(),
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
        };
        return await PostOAuthTokenAsync(form, cancellationToken);
    }

    public async Task RevokeTokenAsync(string token, CancellationToken cancellationToken)
    {
        var value = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthRevokeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = value,
            }),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || (int)response.StatusCode is 400 or 401)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseHttpError(body, "Google could not revoke the OAuth token."));
    }

    public async Task<GcpTokenInfo> GetTokenInfoAsync(string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{OAuthTokenInfoUrl}?access_token={Uri.EscapeDataString(accessToken.Trim())}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseHttpError(body, "Google rejected the OAuth access token."));
        }

        var info = JsonSerializer.Deserialize<GcpTokenInfo>(body, JsonOptions)
                   ?? throw new InvalidOperationException("Google returned an invalid token info response.");
        return info;
    }

    internal static bool FirewallRuleCovers(GcpFirewallProbeRule rule, int port, string expectedCidr)
    {
        if (!string.Equals(rule.Protocol, "tcp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rule.Protocol, "all", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.Ports.Count > 0 && !rule.Ports.Any(spec => PortSpecCovers(spec, port)))
        {
            return false;
        }

        var expected = (expectedCidr ?? string.Empty).Trim();
        return rule.SourceRanges.Any(range =>
        {
            var actual = (range ?? string.Empty).Trim();
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                   || actual is "0.0.0.0/0" or "::/0";
        });
    }

    internal static bool FirewallRuleOpensPortPublicly(GcpFirewallProbeRule rule, int port)
        => (string.Equals(rule.Protocol, "tcp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.Protocol, "all", StringComparison.OrdinalIgnoreCase))
           && (rule.Ports.Count == 0 || rule.Ports.Any(spec => PortSpecCovers(spec, port)))
           && rule.SourceRanges.Any(range => range is "0.0.0.0/0" or "::/0");

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

    internal static string BuildFirewallName(string resourceId, int port)
    {
        var resource = SanitizeFirewallFragment(resourceId);
        if (resource.Length > 48)
        {
            resource = resource[..48].Trim('-');
        }

        var name = $"azp-{resource}-p{port}";
        return name.Length <= 63 ? name : name[..63].Trim('-');
    }

    private static string SanitizeFirewallFragment(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "stack";
        }

        var chars = trimmed.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var sanitized = new string(chars).Trim('-');
        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "stack" : sanitized;
    }

    private static bool PortSpecCovers(string? ports, int port)
    {
        var spec = (ports ?? string.Empty).Trim();
        if (string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase) || spec == "0")
        {
            return true;
        }

        if (int.TryParse(spec, out var single))
        {
            return single == port;
        }

        var dash = spec.IndexOf('-');
        if (dash <= 0 || dash >= spec.Length - 1)
        {
            return false;
        }

        return int.TryParse(spec[..dash], out var from)
               && int.TryParse(spec[(dash + 1)..], out var to)
               && port >= from
               && port <= to;
    }

    private static bool TryParseInstanceId(string instanceId, out string zone, out string name)
    {
        zone = string.Empty;
        name = string.Empty;
        var slash = instanceId.IndexOf('/');
        if (slash <= 0 || slash >= instanceId.Length - 1)
        {
            return false;
        }

        zone = instanceId[..slash].Trim();
        name = instanceId[(slash + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(zone) && !string.IsNullOrWhiteSpace(name);
    }

    private static GcpComputeInstance ToComputeInstance(Instance instance, string zone)
    {
        var imageHint = BuildImageHint(instance);
        return new GcpComputeInstance
        {
            Id = $"{zone}/{instance.Name}",
            Name = instance.Name,
            Zone = zone,
            State = instance.Status,
            PublicHost = ResolvePublicHost(instance),
            Image = imageHint,
            MachineType = ParseResourceName(instance.MachineType),
            SuggestedSshUser = SuggestSshUser(imageHint),
            HasPlatformTag = instance.Tags?.Items?.Contains(PlatformNetworkTag, StringComparer.OrdinalIgnoreCase) == true,
        };
    }

    private static async Task EnsurePlatformTagAsync(
        InstancesClient instancesClient,
        string projectId,
        string zone,
        Instance instance,
        CancellationToken cancellationToken)
    {
        var tags = instance.Tags ?? new Tags();
        if (tags.Items.Contains(PlatformNetworkTag, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        tags.Items.Add(PlatformNetworkTag);
        var operation = await instancesClient.SetTagsAsync(
            new SetTagsInstanceRequest
            {
                Project = projectId,
                Zone = zone,
                Instance = instance.Name,
                TagsResource = tags,
            },
            cancellationToken);
        await operation.PollUntilCompletedAsync();
    }

    private static void RequireProjectId(GcpAccess access)
    {
        if (string.IsNullOrWhiteSpace(access.ProjectId))
        {
            throw new InvalidOperationException(
                "Select a Google Cloud project before listing or launching VMs.");
        }
    }

    private async Task<string> ResolveBearerTokenAsync(GcpAccess access, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(access.AccessToken))
        {
            return access.AccessToken.Trim();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var token = await access.Credential.UnderlyingCredential.GetAccessTokenForRequestAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("GCP credentials did not produce an access token.");
        }

        return token;
    }

    private async Task<GcpOAuthToken> PostOAuthTokenAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseHttpError(body, "Google OAuth token request failed."));
        }

        var token = JsonSerializer.Deserialize<GcpOAuthToken>(body, JsonOptions)
                    ?? throw new InvalidOperationException("Google returned an invalid OAuth token response.");
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Google did not return an access token.");
        }

        return token;
    }

    private static bool IsNotFound(Exception exception)
        => exception is RpcException { StatusCode: StatusCode.NotFound }
           || exception.InnerException is RpcException { StatusCode: StatusCode.NotFound };

    private static string ParseGcpError(Exception exception, string fallback)
    {
        return exception.Message switch
        {
            { Length: > 0 } message when message.Length <= 400 => message,
            { Length: > 400 } message => message[..400] + "…",
            _ => fallback,
        };
    }

    private static string ParseHttpError(string body, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error_description", out var description)
                && description.GetString() is { Length: > 0 } descriptionText)
            {
                return descriptionText;
            }

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } errorText)
                {
                    return errorText;
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.GetString() is { Length: > 0 } messageText)
                {
                    return messageText;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to raw body.
        }

        var trimmed = (body ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return fallback;
        }

        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }

    public sealed class GcpAccess
    {
        public required GoogleCredential Credential { get; init; }

        public string ProjectId { get; init; } = string.Empty;

        public string? AccessToken { get; init; }
    }

    public sealed class GcpOAuthToken
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }

    public sealed class GcpTokenInfo
    {
        [JsonPropertyName("sub")]
        public string? Subject { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        public string DisplayHint
        {
            get
            {
                var email = (Email ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(email) ? (Subject ?? string.Empty).Trim() : email;
            }
        }
    }

    public sealed class GcpFirewallInboundRule
    {
        public int Port { get; init; }

        public string SourceCidr { get; init; } = "0.0.0.0/0";

        public string? Description { get; init; }
    }

    public sealed class GcpFirewallProbeRule
    {
        public string Name { get; init; } = string.Empty;

        public string Protocol { get; init; } = "tcp";

        public List<string> Ports { get; init; } = [];

        public List<string> SourceRanges { get; init; } = [];

        public List<string> TargetTags { get; init; } = [];

        public bool InstanceHasPlatformTag { get; set; }
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

        public bool HasPlatformTag { get; init; }
    }

    public sealed class GcpCatalogOption
    {
        public string Value { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string? Description { get; init; }
    }

    private sealed class GcpProjectListResponse
    {
        public List<GcpProjectListItem> Projects { get; set; } = [];

        public string? NextPageToken { get; set; }
    }

    private sealed class GcpProjectListItem
    {
        public string? ProjectId { get; set; }

        public string? Name { get; set; }
    }
}
