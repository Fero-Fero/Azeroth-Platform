using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class HetznerCloudClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public HetznerCloudClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri("https://api.hetzner.cloud/v1/");
        }
    }

    public async Task ValidateTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "locations?per_page=1", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ParseErrorMessage(body, "Hetzner Cloud rejected the API token."));
        }
    }

    public async Task<IReadOnlyList<HetznerServer>> ListServersAsync(
        string accessToken,
        string? locationFilter,
        CancellationToken cancellationToken)
    {
        var servers = new List<HetznerServer>();
        var page = 1;
        var filter = (locationFilter ?? string.Empty).Trim();

        while (true)
        {
            using var request = CreateRequest(HttpMethod.Get, $"servers?page={page}&per_page=50", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Hetzner Cloud servers."));
            }

            var payload = JsonSerializer.Deserialize<HetznerServerListResponse>(body, JsonOptions)
                            ?? new HetznerServerListResponse();

            if (payload.Servers.Count == 0)
            {
                break;
            }

            servers.AddRange(payload.Servers.Select(HetznerCloudClient.MapServer));

            if (payload.Meta is null || page >= payload.Meta.LastPage)
            {
                break;
            }

            page += 1;
        }

        return servers
            .Where(server => string.Equals(server.Status, "running", StringComparison.OrdinalIgnoreCase))
            .Where(server => string.IsNullOrWhiteSpace(filter)
                             || string.Equals(server.Datacenter?.Location?.Name, filter, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(server.Datacenter?.Name, filter, StringComparison.OrdinalIgnoreCase))
            .Where(server => !string.IsNullOrWhiteSpace(server.PublicIpv4))
            .ToList();
    }

    public async Task<long> UploadSshKeyAsync(
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

        using var request = CreateRequest(HttpMethod.Post, "ssh_keys", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to upload SSH key to Hetzner Cloud."));
        }

        var created = JsonSerializer.Deserialize<HetznerSshKeyResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Hetzner Cloud returned an invalid SSH key response.");
        return created.SshKey?.Id ?? throw new InvalidOperationException("Hetzner Cloud did not return an SSH key id.");
    }

    public async Task<HetznerServer> CreateServerAsync(
        string accessToken,
        string name,
        string location,
        string serverType,
        string image,
        string userData,
        IReadOnlyList<long> sshKeyIds,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            name,
            location,
            server_type = serverType,
            image,
            user_data = userData,
            ssh_keys = sshKeyIds,
            labels = new Dictionary<string, string> { ["azeroth-platform"] = "" },
        });

        using var request = CreateRequest(HttpMethod.Post, "servers", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to create Hetzner Cloud server."));
        }

        var created = JsonSerializer.Deserialize<HetznerServerResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Hetzner Cloud returned an invalid server response.");
        return MapServer(created.Server ?? throw new InvalidOperationException("Hetzner Cloud did not return a server."));
    }

    public async Task<HetznerServer> GetServerAsync(
        string accessToken,
        long serverId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"servers/{serverId}", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to fetch Hetzner Cloud server."));
        }

        var payload = JsonSerializer.Deserialize<HetznerServerResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Hetzner Cloud returned an invalid server response.");
        return MapServer(payload.Server ?? throw new InvalidOperationException("Hetzner Cloud did not return a server."));
    }

    public async Task<HetznerServer> WaitForRunningServerAsync(
        string accessToken,
        long serverId,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 60;
        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = await GetServerAsync(accessToken, serverId, cancellationToken);
            if (string.Equals(server.Status, "running", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(server.PublicIpv4))
            {
                return server;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException("Timed out waiting for the Hetzner Cloud server to become running.");
    }

    public async Task<IReadOnlyList<HetznerCatalogLocation>> ListLocationsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "locations?per_page=200", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Hetzner Cloud locations."));
        }

        var payload = JsonSerializer.Deserialize<HetznerLocationListResponse>(body, JsonOptions)
                        ?? new HetznerLocationListResponse();

        return payload.Locations
            .OrderBy(location => location.City, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<HetznerCatalogServerType>> ListServerTypesAsync(
        string accessToken,
        string? location,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "server_types?per_page=200", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Hetzner Cloud server types."));
        }

        var payload = JsonSerializer.Deserialize<HetznerServerTypeListResponse>(body, JsonOptions)
                        ?? new HetznerServerTypeListResponse();

        var locationFilter = (location ?? string.Empty).Trim();
        return payload.ServerTypes
            .Where(serverType => serverType.Deprecated is not true)
            .Where(serverType => string.IsNullOrWhiteSpace(locationFilter)
                                 || serverType.Locations.Any(entry =>
                                     entry.Name.Equals(locationFilter, StringComparison.OrdinalIgnoreCase)
                                     && entry.Available))
            .OrderBy(serverType => serverType.Cores)
            .ThenBy(serverType => serverType.Memory)
            .ToList();
    }

    public async Task<IReadOnlyList<HetznerCatalogImage>> ListImagesAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "images?type=system&per_page=200", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Hetzner Cloud images."));
        }

        var payload = JsonSerializer.Deserialize<HetznerImageListResponse>(body, JsonOptions)
                        ?? new HetznerImageListResponse();

        return payload.Images
            .Where(image => IsSupportedImage(image))
            .OrderBy(image => image.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSupportedImage(HetznerCatalogImage image)
    {
        var name = (image.Name ?? string.Empty).ToLowerInvariant();
        return name.Contains("ubuntu", StringComparison.Ordinal)
               || name.Contains("debian", StringComparison.Ordinal);
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
            var error = JsonSerializer.Deserialize<HetznerErrorResponse>(body, JsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Error?.Message))
            {
                return error.Error.Message;
            }
        }
        catch
        {
            // Fall through.
        }

        return body.Length <= 500 ? body : fallback;
    }

    public sealed class HetznerServer
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public HetznerDatacenter? Datacenter { get; init; }

        public HetznerImageRef? Image { get; init; }

        public string PublicIpv4 { get; init; } = string.Empty;

        public string SuggestedSshUser { get; init; } = "root";
    }

    public sealed class HetznerDatacenter
    {
        public string Name { get; init; } = string.Empty;

        public HetznerLocationRef? Location { get; init; }
    }

    public sealed class HetznerLocationRef
    {
        public string Name { get; init; } = string.Empty;
    }

    public sealed class HetznerImageRef
    {
        public string Name { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;
    }

    public sealed class HetznerCatalogLocation
    {
        public string Name { get; init; } = string.Empty;

        public string City { get; init; } = string.Empty;

        public string Country { get; init; } = string.Empty;
    }

    public sealed class HetznerCatalogServerType
    {
        public string Name { get; init; } = string.Empty;

        public int Cores { get; init; }

        public double Memory { get; init; }

        public int Disk { get; init; }

        public bool? Deprecated { get; init; }

        public List<HetznerLocationAvailability> Locations { get; init; } = [];
    }

    public sealed class HetznerLocationAvailability
    {
        public string Name { get; init; } = string.Empty;

        public bool Available { get; init; }
    }

    public sealed class HetznerCatalogImage
    {
        public string Name { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;
    }

    private sealed class HetznerServerListResponse
    {
        [JsonPropertyName("servers")]
        public List<HetznerServerJson> Servers { get; init; } = [];

        public HetznerMeta? Meta { get; init; }
    }

    private sealed class HetznerServerJson
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public HetznerDatacenter? Datacenter { get; init; }

        public HetznerImageRef? Image { get; init; }

        [JsonPropertyName("public_net")]
        public HetznerPublicNet? PublicNet { get; init; }
    }

    private sealed class HetznerPublicNet
    {
        public HetznerIpv4? Ipv4 { get; init; }
    }

    private sealed class HetznerIpv4
    {
        public string Ip { get; init; } = string.Empty;
    }

    private sealed class HetznerServerResponse
    {
        public HetznerServerJson? Server { get; init; }
    }

    private sealed class HetznerSshKeyResponse
    {
        [JsonPropertyName("ssh_key")]
        public HetznerSshKey? SshKey { get; init; }
    }

    private sealed class HetznerSshKey
    {
        public long Id { get; init; }
    }

    private sealed class HetznerLocationListResponse
    {
        public List<HetznerCatalogLocation> Locations { get; init; } = [];
    }

    private sealed class HetznerServerTypeListResponse
    {
        [JsonPropertyName("server_types")]
        public List<HetznerCatalogServerType> ServerTypes { get; init; } = [];
    }

    private sealed class HetznerImageListResponse
    {
        public List<HetznerCatalogImage> Images { get; init; } = [];
    }

    private sealed class HetznerMeta
    {
        [JsonPropertyName("last_page")]
        public int LastPage { get; init; }
    }

    private sealed class HetznerErrorResponse
    {
        public HetznerErrorDetail? Error { get; init; }
    }

    private sealed class HetznerErrorDetail
    {
        public string Message { get; init; } = string.Empty;
    }

    private static HetznerServer MapServer(HetznerServerJson server)
    {
        var imageName = server.Image?.Name ?? server.Image?.Description ?? string.Empty;
        return new HetznerServer
        {
            Id = server.Id,
            Name = server.Name,
            Status = server.Status,
            Datacenter = server.Datacenter,
            Image = server.Image,
            PublicIpv4 = server.PublicNet?.Ipv4?.Ip ?? string.Empty,
            SuggestedSshUser = SuggestSshUserFromImage(imageName),
        };
    }

    internal static string SuggestSshUserFromImage(string imageName)
    {
        var name = (imageName ?? string.Empty).ToLowerInvariant();
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
}
