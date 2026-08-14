using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class DigitalOceanClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public DigitalOceanClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri("https://api.digitalocean.com/");
        }
    }

    public async Task ValidateTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "v2/account", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ParseErrorMessage(body, "DigitalOcean rejected the access token."));
        }
    }

    public async Task<IReadOnlyList<DigitalOceanDroplet>> ListDropletsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var droplets = new List<DigitalOceanDroplet>();
        var page = 1;

        while (true)
        {
            using var request = CreateRequest(HttpMethod.Get, $"v2/droplets?page={page}&per_page=200", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list DigitalOcean droplets."));
            }

            var payload = JsonSerializer.Deserialize<DigitalOceanDropletListResponse>(body, JsonOptions)
                            ?? new DigitalOceanDropletListResponse();

            if (payload.Droplets.Count == 0)
            {
                break;
            }

            droplets.AddRange(payload.Droplets);

            if (payload.Meta is null || page >= payload.Meta.TotalPages)
            {
                break;
            }

            page += 1;
        }

        return droplets;
    }

    public async Task<long> UploadAccountSshKeyAsync(
        string accessToken,
        string name,
        string publicKey,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            name,
            public_key = publicKey,
        });

        using var request = CreateRequest(HttpMethod.Post, "v2/account/keys", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to upload SSH key to DigitalOcean."));
        }

        var created = JsonSerializer.Deserialize<DigitalOceanSshKeyResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("DigitalOcean returned an invalid SSH key response.");
        return created.SshKey?.Id ?? throw new InvalidOperationException("DigitalOcean did not return an SSH key id.");
    }

    public async Task<DigitalOceanDroplet> CreateDropletAsync(
        string accessToken,
        string name,
        string region,
        string size,
        string image,
        string userData,
        IReadOnlyList<long> sshKeyIds,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            name,
            region,
            size,
            image,
            user_data = userData,
            ssh_keys = sshKeyIds,
            tags = new[] { "azeroth-platform" },
        });

        using var request = CreateRequest(HttpMethod.Post, "v2/droplets", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to create DigitalOcean droplet."));
        }

        var created = JsonSerializer.Deserialize<DigitalOceanCreateDropletResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("DigitalOcean returned an invalid droplet response.");
        return created.Droplet ?? throw new InvalidOperationException("DigitalOcean did not return a droplet.");
    }

    public async Task<DigitalOceanDroplet> GetDropletAsync(
        string accessToken,
        long dropletId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"v2/droplets/{dropletId}", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to fetch DigitalOcean droplet."));
        }

        var payload = JsonSerializer.Deserialize<DigitalOceanCreateDropletResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("DigitalOcean returned an invalid droplet response.");
        return payload.Droplet ?? throw new InvalidOperationException("DigitalOcean did not return a droplet.");
    }

    public async Task<DigitalOceanDroplet> WaitForActiveDropletAsync(
        string accessToken,
        long dropletId,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 60;
        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var droplet = await GetDropletAsync(accessToken, dropletId, cancellationToken);
            var publicIp = droplet.Networks?.V4
                .FirstOrDefault(network => string.Equals(network.Type, "public", StringComparison.OrdinalIgnoreCase))
                ?.IpAddress;

            if (string.Equals(droplet.Status, "active", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(publicIp))
            {
                return droplet;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException("Timed out waiting for the DigitalOcean droplet to become active.");
    }

    public async Task<IReadOnlyList<DigitalOceanCatalogRegion>> ListRegionsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "v2/regions?per_page=200", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list DigitalOcean regions."));
        }

        var payload = JsonSerializer.Deserialize<DigitalOceanRegionListResponse>(body, JsonOptions)
                        ?? new DigitalOceanRegionListResponse();

        return payload.Regions
            .Where(region => region.Available)
            .OrderBy(region => region.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<DigitalOceanCatalogSize>> ListSizesAsync(
        string accessToken,
        string? region,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "v2/sizes?per_page=200", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list DigitalOcean sizes."));
        }

        var payload = JsonSerializer.Deserialize<DigitalOceanSizeListResponse>(body, JsonOptions)
                        ?? new DigitalOceanSizeListResponse();

        var regionFilter = (region ?? string.Empty).Trim();
        return payload.Sizes
            .Where(size => size.Available)
            .Where(size => string.IsNullOrWhiteSpace(regionFilter)
                           || size.Regions.Any(entry => entry.Equals(regionFilter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(size => size.Vcpus)
            .ThenBy(size => size.Memory)
            .ToList();
    }

    public async Task<IReadOnlyList<DigitalOceanCatalogImage>> ListDistributionImagesAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var images = new List<DigitalOceanCatalogImage>();
        var page = 1;

        while (true)
        {
            using var request = CreateRequest(
                HttpMethod.Get,
                $"v2/images?type=distribution&per_page=200&page={page}",
                accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list DigitalOcean images."));
            }

            var payload = JsonSerializer.Deserialize<DigitalOceanImageListResponse>(body, JsonOptions)
                            ?? new DigitalOceanImageListResponse();

            if (payload.Images.Count == 0)
            {
                break;
            }

            images.AddRange(payload.Images.Where(image => !string.IsNullOrWhiteSpace(image.Slug)));

            if (payload.Meta is null || page >= payload.Meta.TotalPages)
            {
                break;
            }

            page += 1;
        }

        return images
            .Where(image => IsSupportedDistributionImage(image))
            .OrderBy(image => image.Distribution, StringComparer.OrdinalIgnoreCase)
            .ThenBy(image => image.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSupportedDistributionImage(DigitalOceanCatalogImage image)
    {
        var distribution = (image.Distribution ?? string.Empty).ToLowerInvariant();
        if (distribution is not ("ubuntu" or "debian" or "centos" or "fedora"))
        {
            return false;
        }

        var slug = (image.Slug ?? string.Empty).ToLowerInvariant();
        return slug.Contains("x64", StringComparison.Ordinal)
               || slug.Contains("amd64", StringComparison.Ordinal);
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
            var error = JsonSerializer.Deserialize<DigitalOceanErrorResponse>(body, JsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                return error.Message;
            }
        }
        catch
        {
            // Fall through to raw body.
        }

        return body.Length > 300 ? body[..300] + "…" : body;
    }

    public sealed class DigitalOceanDropletListResponse
    {
        [JsonPropertyName("droplets")]
        public List<DigitalOceanDroplet> Droplets { get; set; } = [];

        [JsonPropertyName("meta")]
        public DigitalOceanMeta? Meta { get; set; }
    }

    public sealed class DigitalOceanMeta
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("total_pages")]
        public int TotalPages { get; set; }
    }

    public sealed class DigitalOceanDroplet
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public DigitalOceanRegion? Region { get; set; }

        [JsonPropertyName("image")]
        public DigitalOceanImage? Image { get; set; }

        [JsonPropertyName("networks")]
        public DigitalOceanNetworks? Networks { get; set; }
    }

    public sealed class DigitalOceanRegion
    {
        [JsonPropertyName("slug")]
        public string Slug { get; set; } = string.Empty;
    }

    public sealed class DigitalOceanImage
    {
        [JsonPropertyName("slug")]
        public string Slug { get; set; } = string.Empty;

        [JsonPropertyName("distribution")]
        public string Distribution { get; set; } = string.Empty;
    }

    public sealed class DigitalOceanNetworks
    {
        [JsonPropertyName("v4")]
        public List<DigitalOceanNetworkAddress> V4 { get; set; } = [];
    }

    public sealed class DigitalOceanNetworkAddress
    {
        [JsonPropertyName("ip_address")]
        public string IpAddress { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }

    private sealed class DigitalOceanErrorResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class DigitalOceanSshKeyResponse
    {
        [JsonPropertyName("ssh_key")]
        public DigitalOceanSshKey? SshKey { get; set; }
    }

    private sealed class DigitalOceanSshKey
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }

    private sealed class DigitalOceanCreateDropletResponse
    {
        [JsonPropertyName("droplet")]
        public DigitalOceanDroplet? Droplet { get; set; }
    }

    public sealed class DigitalOceanCatalogRegion
    {
        [JsonPropertyName("slug")]
        public string Slug { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("available")]
        public bool Available { get; set; }
    }

    public sealed class DigitalOceanCatalogSize
    {
        [JsonPropertyName("slug")]
        public string Slug { get; set; } = string.Empty;

        [JsonPropertyName("memory")]
        public int Memory { get; set; }

        [JsonPropertyName("vcpus")]
        public int Vcpus { get; set; }

        [JsonPropertyName("disk")]
        public int Disk { get; set; }

        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("regions")]
        public List<string> Regions { get; set; } = [];
    }

    public sealed class DigitalOceanCatalogImage
    {
        [JsonPropertyName("slug")]
        public string Slug { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("distribution")]
        public string Distribution { get; set; } = string.Empty;
    }

    private sealed class DigitalOceanRegionListResponse
    {
        [JsonPropertyName("regions")]
        public List<DigitalOceanCatalogRegion> Regions { get; set; } = [];
    }

    private sealed class DigitalOceanSizeListResponse
    {
        [JsonPropertyName("sizes")]
        public List<DigitalOceanCatalogSize> Sizes { get; set; } = [];
    }

    private sealed class DigitalOceanImageListResponse
    {
        [JsonPropertyName("images")]
        public List<DigitalOceanCatalogImage> Images { get; set; } = [];

        [JsonPropertyName("meta")]
        public DigitalOceanMeta? Meta { get; set; }
    }
}
