using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class VultrClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public VultrClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri("https://api.vultr.com/v2/");
        }
    }

    public async Task ValidateTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "regions", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ParseErrorMessage(body, "Vultr rejected the API token."));
        }
    }

    public async Task<IReadOnlyList<VultrInstance>> ListInstancesAsync(
        string accessToken,
        string? regionFilter,
        CancellationToken cancellationToken)
    {
        var instances = new List<VultrInstance>();
        var cursor = string.Empty;
        var filter = (regionFilter ?? string.Empty).Trim();

        while (true)
        {
            var path = string.IsNullOrWhiteSpace(cursor)
                ? "instances?per_page=100"
                : $"instances?per_page=100&cursor={Uri.EscapeDataString(cursor)}";

            using var request = CreateRequest(HttpMethod.Get, path, accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Vultr instances."));
            }

            var payload = JsonSerializer.Deserialize<VultrInstanceListResponse>(body, JsonOptions)
                            ?? new VultrInstanceListResponse();

            if (payload.Instances.Count == 0)
            {
                break;
            }

            instances.AddRange(payload.Instances.Select(MapInstance));

            if (payload.Meta is null || string.IsNullOrWhiteSpace(payload.Meta.Next))
            {
                break;
            }

            cursor = payload.Meta.Next;
        }

        return instances
            .Where(instance => string.Equals(instance.Status, "active", StringComparison.OrdinalIgnoreCase))
            .Where(instance => string.IsNullOrWhiteSpace(filter)
                               || string.Equals(instance.Region, filter, StringComparison.OrdinalIgnoreCase))
            .Where(instance => !string.IsNullOrWhiteSpace(instance.PublicHost))
            .ToList();
    }

    public async Task<string> UploadSshKeyAsync(
        string accessToken,
        string name,
        string publicKey,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            name,
            ssh_key = publicKey,
        });

        using var request = CreateRequest(HttpMethod.Post, "ssh-keys", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to upload SSH key to Vultr."));
        }

        var created = JsonSerializer.Deserialize<VultrSshKeyResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Vultr returned an invalid SSH key response.");
        return created.SshKey?.Id ?? throw new InvalidOperationException("Vultr did not return an SSH key id.");
    }

    public async Task<VultrInstance> CreateInstanceAsync(
        string accessToken,
        string label,
        string region,
        string plan,
        int osId,
        string userData,
        IReadOnlyList<string> sshKeyIds,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            region,
            plan,
            os_id = osId,
            label,
            user_data = userData,
            sshkey_id = sshKeyIds,
            tags = new[] { "azeroth-platform" },
        });

        using var request = CreateRequest(HttpMethod.Post, "instances", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to create Vultr instance."));
        }

        var created = JsonSerializer.Deserialize<VultrInstanceResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Vultr returned an invalid instance response.");
        return MapInstance(created.Instance ?? throw new InvalidOperationException("Vultr did not return an instance."));
    }

    public async Task<VultrInstance> GetInstanceAsync(
        string accessToken,
        string instanceId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"instances/{instanceId}", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to fetch Vultr instance."));
        }

        var payload = JsonSerializer.Deserialize<VultrInstanceResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Vultr returned an invalid instance response.");
        return MapInstance(payload.Instance ?? throw new InvalidOperationException("Vultr did not return an instance."));
    }

    public async Task<VultrInstance> WaitForActiveInstanceAsync(
        string accessToken,
        string instanceId,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 60;
        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await GetInstanceAsync(accessToken, instanceId, cancellationToken);
            if (string.Equals(instance.Status, "active", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(instance.PublicHost))
            {
                return instance;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException("Timed out waiting for the Vultr instance to become active.");
    }

    public async Task<IReadOnlyList<VultrCatalogRegion>> ListRegionsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "regions", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Vultr regions."));
        }

        var payload = JsonSerializer.Deserialize<VultrRegionListResponse>(body, JsonOptions)
                        ?? new VultrRegionListResponse();

        return payload.Regions
            .OrderBy(region => region.City, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<VultrCatalogPlan>> ListPlansAsync(
        string accessToken,
        string? region,
        CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(region)
            ? "plans?type=vc2&per_page=200"
            : $"plans?type=vc2&per_page=200&region={Uri.EscapeDataString(region.Trim())}";

        using var request = CreateRequest(HttpMethod.Get, path, accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Vultr plans."));
        }

        var payload = JsonSerializer.Deserialize<VultrPlanListResponse>(body, JsonOptions)
                        ?? new VultrPlanListResponse();

        return payload.Plans
            .OrderBy(plan => plan.VcpuCount)
            .ThenBy(plan => plan.Ram)
            .ToList();
    }

    public async Task<IReadOnlyList<VultrCatalogOs>> ListOperatingSystemsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "os?per_page=200", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Vultr operating systems."));
        }

        var payload = JsonSerializer.Deserialize<VultrOsListResponse>(body, JsonOptions)
                        ?? new VultrOsListResponse();

        return payload.Os
            .Where(os => IsSupportedOs(os))
            .OrderBy(os => os.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSupportedOs(VultrCatalogOs os)
    {
        var name = (os.Name ?? string.Empty).ToLowerInvariant();
        return name.Contains("ubuntu", StringComparison.Ordinal)
               || name.Contains("debian", StringComparison.Ordinal);
    }

    private static VultrInstance MapInstance(VultrInstanceJson instance)
    {
        var osName = instance.Os ?? instance.OsId.ToString();
        return new VultrInstance
        {
            Id = instance.Id,
            Label = instance.Label,
            Region = instance.Region,
            Status = instance.Status,
            PublicHost = instance.MainIp,
            Os = osName,
            SuggestedSshUser = SuggestSshUserFromOs(osName),
        };
    }

    internal static string SuggestSshUserFromOs(string osName)
    {
        var name = (osName ?? string.Empty).ToLowerInvariant();
        if (name.Contains("ubuntu", StringComparison.Ordinal))
        {
            return "root";
        }

        if (name.Contains("debian", StringComparison.Ordinal))
        {
            return "root";
        }

        return "root";
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string ParseErrorMessage(string body, string fallback)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return fallback;
        }

        try
        {
            var error = JsonSerializer.Deserialize<VultrErrorResponse>(body, JsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error;
            }
        }
        catch
        {
            // Fall through.
        }

        return body.Length <= 500 ? body : fallback;
    }

    public sealed class VultrInstance
    {
        public string Id { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string Region { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public string PublicHost { get; init; } = string.Empty;

        public string Os { get; init; } = string.Empty;

        public string SuggestedSshUser { get; init; } = "root";
    }

    public sealed class VultrCatalogRegion
    {
        public string Id { get; init; } = string.Empty;

        public string City { get; init; } = string.Empty;

        public string Country { get; init; } = string.Empty;
    }

    public sealed class VultrCatalogPlan
    {
        public string Id { get; init; } = string.Empty;

        public int VcpuCount { get; init; }

        public int Ram { get; init; }

        public int Disk { get; init; }
    }

    public sealed class VultrCatalogOs
    {
        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    private sealed class VultrInstanceListResponse
    {
        public List<VultrInstanceJson> Instances { get; init; } = [];

        public VultrMeta? Meta { get; init; }
    }

    private sealed class VultrInstanceJson
    {
        public string Id { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string Region { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        [JsonPropertyName("main_ip")]
        public string MainIp { get; init; } = string.Empty;

        public string Os { get; init; } = string.Empty;

        [JsonPropertyName("os_id")]
        public int OsId { get; init; }
    }

    private sealed class VultrInstanceResponse
    {
        public VultrInstanceJson? Instance { get; init; }
    }

    private sealed class VultrSshKeyResponse
    {
        [JsonPropertyName("ssh_key")]
        public VultrSshKey? SshKey { get; init; }
    }

    private sealed class VultrSshKey
    {
        public string Id { get; init; } = string.Empty;
    }

    private sealed class VultrRegionListResponse
    {
        public List<VultrCatalogRegion> Regions { get; init; } = [];
    }

    private sealed class VultrPlanListResponse
    {
        public List<VultrCatalogPlan> Plans { get; init; } = [];
    }

    private sealed class VultrOsListResponse
    {
        public List<VultrCatalogOs> Os { get; init; } = [];
    }

    private sealed class VultrMeta
    {
        public string Next { get; init; } = string.Empty;
    }

    private sealed class VultrErrorResponse
    {
        public string Error { get; init; } = string.Empty;
    }
}
