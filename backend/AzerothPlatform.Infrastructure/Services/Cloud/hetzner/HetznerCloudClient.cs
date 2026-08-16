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

    public async Task DeleteServerAsync(
        string accessToken,
        long serverId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"servers/{serverId}", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseErrorMessage(body, "Failed to delete Hetzner Cloud server."));
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

    public async Task<HetznerServer?> FindServerAsync(
        string accessToken,
        string? instanceId,
        string? publicHost,
        CancellationToken cancellationToken)
    {
        var id = (instanceId ?? string.Empty).Trim();
        if (long.TryParse(id, out var serverId) && serverId > 0)
        {
            try
            {
                return await GetServerAsync(accessToken, serverId, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Fall through to public-IP search.
            }
        }

        var host = (publicHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var servers = await ListServersAsync(accessToken, locationFilter: null, cancellationToken);
        return servers.FirstOrDefault(server =>
            string.Equals(server.PublicIpv4, host, StringComparison.OrdinalIgnoreCase)
            || string.Equals(server.Id.ToString(), host, StringComparison.OrdinalIgnoreCase));
    }

    public async Task ProbeWriteAccessAsync(string accessToken, CancellationToken cancellationToken)
    {
        var name = $"azp-probe-{Guid.NewGuid():N}"[..18];
        try
        {
            var created = await CreateFirewallAsync(
                accessToken,
                name,
                [
                    new HetznerFirewallInboundRule
                    {
                        Port = "22",
                        SourceIps = ["127.0.0.1/32"],
                        Description = "Azeroth Platform write probe",
                    },
                ],
                serverId: null,
                cancellationToken);
            try
            {
                await DeleteFirewallAsync(accessToken, created.Id, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Probe succeeded; leftover probe firewall can be deleted in the console.
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "This Hetzner token is read-only or cannot manage Cloud Firewalls. Create a Read & Write project token in Hetzner Console → Security → API tokens.",
                ex);
        }
    }

    public async Task<HetznerFirewall> ApplyFirewallAsync(
        string accessToken,
        string firewallName,
        long serverId,
        IReadOnlyList<HetznerFirewallInboundRule> inboundRules,
        CancellationToken cancellationToken)
    {
        var name = SanitizeFirewallName(firewallName);
        var existing = await ListFirewallsAsync(accessToken, cancellationToken);
        var match = existing.FirstOrDefault(firewall =>
                        string.Equals(firewall.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? existing.FirstOrDefault(firewall =>
                        firewall.ServerIds.Contains(serverId)
                        && firewall.Name.StartsWith("azeroth-platform", StringComparison.OrdinalIgnoreCase));

        var merged = MergeInboundRules(match?.Rules ?? [], inboundRules);
        if (match is null)
        {
            return await CreateFirewallAsync(accessToken, name, merged, serverId, cancellationToken);
        }

        await SetFirewallRulesAsync(accessToken, match.Id, merged, cancellationToken);
        if (!match.ServerIds.Contains(serverId))
        {
            await ApplyFirewallToServerAsync(accessToken, match.Id, serverId, cancellationToken);
        }

        return match with { Rules = merged.ToList(), ServerIds = match.ServerIds.Contains(serverId) ? match.ServerIds : [.. match.ServerIds, serverId] };
    }

    public async Task<IReadOnlyList<HetznerFirewallInboundRule>> ListServerInboundRulesAsync(
        string accessToken,
        long serverId,
        CancellationToken cancellationToken)
    {
        var firewalls = await ListFirewallsAsync(accessToken, cancellationToken);
        return firewalls
            .Where(firewall => firewall.ServerIds.Contains(serverId))
            .SelectMany(firewall => firewall.Rules)
            .Where(rule => string.Equals(rule.Direction, "in", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    internal static bool FirewallRuleCovers(HetznerFirewallInboundRule rule, int port, string expectedCidr)
    {
        if (!string.Equals(rule.Direction, "in", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(rule.Direction))
        {
            return false;
        }

        if (!string.Equals(rule.Protocol, "tcp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rule.Protocol, string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!PortSpecCovers(rule.Port, port))
        {
            return false;
        }

        var expected = (expectedCidr ?? string.Empty).Trim();
        return rule.SourceIps.Any(source =>
            string.Equals(source.Trim(), expected, StringComparison.OrdinalIgnoreCase)
            || source.Trim() is "0.0.0.0/0" or "::/0");
    }

    internal static bool FirewallRuleOpensPortPublicly(HetznerFirewallInboundRule rule, int port)
        => PortSpecCovers(rule.Port, port)
           && (string.IsNullOrWhiteSpace(rule.Direction)
               || string.Equals(rule.Direction, "in", StringComparison.OrdinalIgnoreCase))
           && rule.SourceIps.Any(source => source.Trim() is "0.0.0.0/0" or "::/0");

    internal static string SanitizeFirewallName(string value)
    {
        var chars = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var name = new string(chars).Trim('-');
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "azeroth-platform";
        }

        return name.Length <= 64 ? name : name[..64].Trim('-');
    }

    internal static string MaskToken(string token)
    {
        var value = (token ?? string.Empty).Trim();
        if (value.Length < 8)
        {
            return "Hetzner project";
        }

        return $"Token …{value[^4..]}";
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

    private async Task<IReadOnlyList<HetznerFirewall>> ListFirewallsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var firewalls = new List<HetznerFirewall>();
        var page = 1;
        while (true)
        {
            using var request = CreateRequest(HttpMethod.Get, $"firewalls?page={page}&per_page=50", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Hetzner Cloud Firewalls."));
            }

            var payload = JsonSerializer.Deserialize<HetznerFirewallListResponse>(body, JsonOptions)
                          ?? new HetznerFirewallListResponse();
            if (payload.Firewalls.Count == 0)
            {
                break;
            }

            firewalls.AddRange(payload.Firewalls.Select(MapFirewall));
            var lastPage = payload.Meta?.Pagination?.LastPage ?? payload.Meta?.LastPage ?? page;
            if (page >= lastPage)
            {
                break;
            }

            page += 1;
        }

        return firewalls;
    }

    private async Task<HetznerFirewall> CreateFirewallAsync(
        string accessToken,
        string name,
        IReadOnlyList<HetznerFirewallInboundRule> inboundRules,
        long? serverId,
        CancellationToken cancellationToken)
    {
        object payload = serverId is > 0
            ? new
            {
                name,
                rules = inboundRules.Select(ToApiRule).ToList(),
                apply_to = new object[] { new { type = "server", server = new { id = serverId.Value } } },
            }
            : new
            {
                name,
                rules = inboundRules.Select(ToApiRule).ToList(),
            };

        using var request = CreateRequest(HttpMethod.Post, "firewalls", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to create Hetzner Cloud Firewall."));
        }

        var created = JsonSerializer.Deserialize<HetznerFirewallResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Hetzner Cloud returned an invalid firewall response.");
        return MapFirewall(created.Firewall ?? throw new InvalidOperationException("Hetzner Cloud did not return a firewall."));
    }

    private async Task SetFirewallRulesAsync(
        string accessToken,
        long firewallId,
        IReadOnlyList<HetznerFirewallInboundRule> inboundRules,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { rules = inboundRules.Select(ToApiRule).ToList() });
        using var request = CreateRequest(HttpMethod.Post, $"firewalls/{firewallId}/actions/set_rules", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseErrorMessage(body, "Failed to update Hetzner Cloud Firewall rules."));
    }

    private async Task ApplyFirewallToServerAsync(
        string accessToken,
        long firewallId,
        long serverId,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            apply_to = new[] { new { type = "server", server = new { id = serverId } } },
        });
        using var request = CreateRequest(HttpMethod.Post, $"firewalls/{firewallId}/actions/apply_to_resources", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseErrorMessage(body, "Failed to apply Hetzner Cloud Firewall to the server."));
    }

    private async Task DeleteFirewallAsync(string accessToken, long firewallId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"firewalls/{firewallId}", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseErrorMessage(body, "Failed to delete Hetzner Cloud Firewall."));
    }

    private static object ToApiRule(HetznerFirewallInboundRule rule)
        => new
        {
            direction = "in",
            protocol = "tcp",
            port = rule.Port,
            source_ips = rule.SourceIps.Count > 0 ? rule.SourceIps : new List<string> { "0.0.0.0/0" },
            description = string.IsNullOrWhiteSpace(rule.Description)
                ? $"Azeroth Platform tcp/{rule.Port}"
                : rule.Description.Trim(),
        };

    private static List<HetznerFirewallInboundRule> MergeInboundRules(
        IEnumerable<HetznerFirewallInboundRule> existing,
        IReadOnlyList<HetznerFirewallInboundRule> incoming)
    {
        var merged = existing
            .Where(rule => string.Equals(rule.Direction, "in", StringComparison.OrdinalIgnoreCase)
                           || string.IsNullOrWhiteSpace(rule.Direction))
            .ToList();
        foreach (var rule in incoming)
        {
            if (!int.TryParse(rule.Port, out var port))
            {
                merged.Add(rule);
                continue;
            }

            var cidr = rule.SourceIps.FirstOrDefault() ?? "0.0.0.0/0";
            if (!merged.Any(item => FirewallRuleCovers(item, port, cidr)))
            {
                merged.Add(rule);
            }
        }

        return merged;
    }

    private static bool PortSpecCovers(string? ports, int port)
    {
        var spec = (ports ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(spec) || spec is "*" or "any")
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
            return spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(part => PortSpecCovers(part, port));
        }

        return int.TryParse(spec[..dash], out var from)
               && int.TryParse(spec[(dash + 1)..], out var to)
               && port >= from
               && port <= to;
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

        public string ServerType { get; init; } = string.Empty;

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

    public sealed record HetznerFirewall
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public List<HetznerFirewallInboundRule> Rules { get; init; } = [];

        public List<long> ServerIds { get; init; } = [];
    }

    public sealed class HetznerFirewallInboundRule
    {
        public string Direction { get; init; } = "in";

        public string Protocol { get; init; } = "tcp";

        public string Port { get; init; } = string.Empty;

        public List<string> SourceIps { get; init; } = [];

        public string? Description { get; init; }
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

        [JsonPropertyName("server_type")]
        public HetznerServerTypeRef? ServerType { get; init; }
    }

    private sealed class HetznerServerTypeRef
    {
        public string Name { get; init; } = string.Empty;
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
        public HetznerPagination? Pagination { get; init; }

        [JsonPropertyName("last_page")]
        public int LastPage { get; init; }
    }

    private sealed class HetznerPagination
    {
        [JsonPropertyName("last_page")]
        public int LastPage { get; init; }
    }

    private sealed class HetznerFirewallListResponse
    {
        [JsonPropertyName("firewalls")]
        public List<HetznerFirewallJson> Firewalls { get; init; } = [];

        public HetznerMeta? Meta { get; init; }
    }

    private sealed class HetznerFirewallResponse
    {
        public HetznerFirewallJson? Firewall { get; init; }
    }

    private sealed class HetznerFirewallJson
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public List<HetznerFirewallRuleJson> Rules { get; init; } = [];

        [JsonPropertyName("applied_to")]
        public List<HetznerFirewallAppliedTo> AppliedTo { get; init; } = [];
    }

    private sealed class HetznerFirewallRuleJson
    {
        public string Direction { get; init; } = "in";

        public string Protocol { get; init; } = "tcp";

        public string Port { get; init; } = string.Empty;

        [JsonPropertyName("source_ips")]
        public List<string> SourceIps { get; init; } = [];

        public string? Description { get; init; }
    }

    private sealed class HetznerFirewallAppliedTo
    {
        public string Type { get; init; } = string.Empty;

        public HetznerFirewallAppliedServer? Server { get; init; }
    }

    private sealed class HetznerFirewallAppliedServer
    {
        public long Id { get; init; }
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
            ServerType = server.ServerType?.Name ?? string.Empty,
            SuggestedSshUser = SuggestSshUserFromImage(imageName),
        };
    }

    private static HetznerFirewall MapFirewall(HetznerFirewallJson firewall)
        => new()
        {
            Id = firewall.Id,
            Name = firewall.Name,
            Rules = firewall.Rules
                .Select(rule => new HetznerFirewallInboundRule
                {
                    Direction = rule.Direction,
                    Protocol = rule.Protocol,
                    Port = rule.Port,
                    SourceIps = rule.SourceIps,
                    Description = rule.Description,
                })
                .ToList(),
            ServerIds = firewall.AppliedTo
                .Where(item => string.Equals(item.Type, "server", StringComparison.OrdinalIgnoreCase)
                               && item.Server is not null)
                .Select(item => item.Server!.Id)
                .ToList(),
        };

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
