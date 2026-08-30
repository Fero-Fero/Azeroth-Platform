using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Core.Contracts;

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

    public const string OAuthAuthorizeUrl = "https://cloud.digitalocean.com/v1/oauth/authorize";

    public const string OAuthTokenUrl = "https://cloud.digitalocean.com/v1/oauth/token";

    public const string OAuthRevokeUrl = "https://cloud.digitalocean.com/v1/oauth/revoke";

    public const string OAuthScopes = "read write";

    public async Task ValidateTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        _ = await GetAccountAsync(accessToken, cancellationToken);
    }

    public async Task<DigitalOceanAccount> GetAccountAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "v2/account", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "DigitalOcean rejected the access token."));
        }

        var payload = JsonSerializer.Deserialize<DigitalOceanAccountResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("DigitalOcean returned an invalid account response.");
        var account = payload.Account ?? throw new InvalidOperationException("DigitalOcean did not return an account.");
        return account;
    }

    public async Task<DigitalOceanOAuthToken> ExchangeAuthorizationCodeAsync(
        string clientId,
        string clientSecret,
        string redirectUri,
        string code,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code.Trim(),
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
            ["redirect_uri"] = redirectUri.Trim(),
        };
        return await PostOAuthTokenAsync(form, cancellationToken);
    }

    public async Task<DigitalOceanOAuthToken> RefreshAccessTokenAsync(
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

    public async Task RevokeTokenAsync(
        string clientId,
        string clientSecret,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthRevokeUrl);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId.Trim()}:{clientSecret.Trim()}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token.Trim(),
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || (int)response.StatusCode is 400 or 401 or 404)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseErrorMessage(body, "DigitalOcean could not revoke the OAuth token."));
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

    public async Task DeleteDropletAsync(
        string accessToken,
        long dropletId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"v2/droplets/{dropletId}", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseErrorMessage(body, "Failed to delete DigitalOcean droplet."));
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

    public async Task<DigitalOceanDroplet?> FindDropletAsync(
        string accessToken,
        string? dropletId,
        string? publicHost,
        CancellationToken cancellationToken)
    {
        if (long.TryParse((dropletId ?? string.Empty).Trim(), out var parsedId) && parsedId > 0)
        {
            try
            {
                return await GetDropletAsync(accessToken, parsedId, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Fall through to public-IP lookup.
            }
        }

        var host = (publicHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var droplets = await ListDropletsAsync(accessToken, cancellationToken);
        return droplets.FirstOrDefault(droplet =>
            droplet.Networks?.V4.Any(network =>
                string.Equals(network.IpAddress, host, StringComparison.OrdinalIgnoreCase)) == true);
    }

    public async Task<DigitalOceanFirewall> ApplyDropletFirewallAsync(
        string accessToken,
        string firewallName,
        long dropletId,
        IReadOnlyList<DigitalOceanFirewallInboundRule> inboundRules,
        CancellationToken cancellationToken)
    {
        var existing = await ListFirewallsAsync(accessToken, cancellationToken);
        var match = existing.FirstOrDefault(firewall =>
                        string.Equals(firewall.Name, firewallName, StringComparison.OrdinalIgnoreCase))
                    ?? existing.FirstOrDefault(firewall =>
                        firewall.DropletIds.Contains(dropletId)
                        && firewall.Name.StartsWith("azeroth-platform", StringComparison.OrdinalIgnoreCase));

        var dropletIds = match is null
            ? new List<long> { dropletId }
            : match.DropletIds.Contains(dropletId)
                ? match.DropletIds.ToList()
                : [.. match.DropletIds, dropletId];

        if (match is null)
        {
            return await CreateFirewallAsync(
                accessToken,
                firewallName,
                inboundRules,
                dropletIds,
                cancellationToken);
        }

        return await ReplaceFirewallAsync(
            accessToken,
            match.Id,
            match.Name,
            inboundRules,
            dropletIds,
            cancellationToken);
    }

    public async Task<IReadOnlyList<DigitalOceanFirewallInboundRule>> ListDropletInboundRulesAsync(
        string accessToken,
        long dropletId,
        CancellationToken cancellationToken)
    {
        var firewalls = await ListFirewallsAsync(accessToken, cancellationToken);
        return firewalls
            .Where(firewall => firewall.DropletIds.Contains(dropletId))
            .SelectMany(firewall => firewall.InboundRules)
            .ToList();
    }

    internal static bool InboundRuleCovers(
        DigitalOceanFirewallInboundRule rule,
        int port,
        string expectedCidr,
        bool adminSshUnpinned = false)
    {
        if (!string.Equals(rule.Protocol, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!PortSpecCovers(rule.Ports, port))
        {
            return false;
        }

        return rule.SourceAddresses.Any(address =>
            VpcSecurityCatalog.ProbeIngressSourceSatisfied(expectedCidr, address, adminSshUnpinned));
    }

    internal static bool InboundRuleOpensPortPublicly(DigitalOceanFirewallInboundRule rule, int port)
        => string.Equals(rule.Protocol, "tcp", StringComparison.OrdinalIgnoreCase)
           && PortSpecCovers(rule.Ports, port)
           && rule.SourceAddresses.Any(address => address is "0.0.0.0/0" or "::/0");

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

    private async Task<IReadOnlyList<DigitalOceanFirewall>> ListFirewallsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var firewalls = new List<DigitalOceanFirewall>();
        var page = 1;
        while (true)
        {
            using var request = CreateRequest(HttpMethod.Get, $"v2/firewalls?page={page}&per_page=200", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list DigitalOcean firewalls."));
            }

            var payload = JsonSerializer.Deserialize<DigitalOceanFirewallListResponse>(body, JsonOptions)
                          ?? new DigitalOceanFirewallListResponse();
            firewalls.AddRange(payload.Firewalls);
            if (payload.Meta is null || page >= payload.Meta.TotalPages || payload.Firewalls.Count == 0)
            {
                break;
            }

            page += 1;
        }

        return firewalls;
    }

    private async Task<DigitalOceanFirewall> CreateFirewallAsync(
        string accessToken,
        string name,
        IReadOnlyList<DigitalOceanFirewallInboundRule> inboundRules,
        IReadOnlyList<long> dropletIds,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "v2/firewalls", accessToken);
        request.Content = JsonContent(new DigitalOceanFirewallWriteRequest
        {
            Name = name,
            InboundRules = inboundRules.Select(ToApiInbound).ToList(),
            OutboundRules = DefaultOutboundRules(),
            DropletIds = dropletIds.ToList(),
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to create DigitalOcean Cloud Firewall."));
        }

        var payload = JsonSerializer.Deserialize<DigitalOceanFirewallResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("DigitalOcean returned an invalid firewall response.");
        return payload.Firewall ?? throw new InvalidOperationException("DigitalOcean did not return a firewall.");
    }

    private async Task<DigitalOceanFirewall> ReplaceFirewallAsync(
        string accessToken,
        string firewallId,
        string name,
        IReadOnlyList<DigitalOceanFirewallInboundRule> inboundRules,
        IReadOnlyList<long> dropletIds,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Put, $"v2/firewalls/{firewallId}", accessToken);
        request.Content = JsonContent(new DigitalOceanFirewallWriteRequest
        {
            Name = name,
            InboundRules = inboundRules.Select(ToApiInbound).ToList(),
            OutboundRules = DefaultOutboundRules(),
            DropletIds = dropletIds.ToList(),
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to update DigitalOcean Cloud Firewall."));
        }

        var payload = JsonSerializer.Deserialize<DigitalOceanFirewallResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("DigitalOcean returned an invalid firewall response.");
        return payload.Firewall ?? throw new InvalidOperationException("DigitalOcean did not return a firewall.");
    }

    private async Task<DigitalOceanOAuthToken> PostOAuthTokenAsync(
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
            throw new InvalidOperationException(ParseErrorMessage(body, "DigitalOcean OAuth token exchange failed."));
        }

        var payload = JsonSerializer.Deserialize<DigitalOceanOAuthToken>(body, JsonOptions)
                      ?? throw new InvalidOperationException("DigitalOcean returned an invalid OAuth token response.");
        if (string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new InvalidOperationException("DigitalOcean did not return an access token.");
        }

        return payload;
    }

    private static StringContent JsonContent(object payload)
        => new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private static DigitalOceanFirewallApiInbound ToApiInbound(DigitalOceanFirewallInboundRule rule)
        => new()
        {
            Protocol = string.IsNullOrWhiteSpace(rule.Protocol) ? "tcp" : rule.Protocol,
            Ports = rule.Ports,
            Sources = new DigitalOceanFirewallAddresses { Addresses = rule.SourceAddresses.ToList() },
        };

    private static List<DigitalOceanFirewallApiOutbound> DefaultOutboundRules()
        =>
        [
            new()
            {
                Protocol = "icmp",
                Destinations = new DigitalOceanFirewallAddresses { Addresses = ["0.0.0.0/0", "::/0"] },
            },
            new()
            {
                Protocol = "tcp",
                Ports = "all",
                Destinations = new DigitalOceanFirewallAddresses { Addresses = ["0.0.0.0/0", "::/0"] },
            },
            new()
            {
                Protocol = "udp",
                Ports = "all",
                Destinations = new DigitalOceanFirewallAddresses { Addresses = ["0.0.0.0/0", "::/0"] },
            },
        ];

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

        [JsonPropertyName("size_slug")]
        public string SizeSlug { get; set; } = string.Empty;

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

    public sealed class DigitalOceanAccount
    {
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("team")]
        public DigitalOceanTeam? Team { get; set; }

        public string DisplayHint
        {
            get
            {
                var team = (Team?.Name ?? string.Empty).Trim();
                var email = Email.Trim();
                if (!string.IsNullOrWhiteSpace(team) && !string.IsNullOrWhiteSpace(email))
                {
                    return $"{email} ({team})";
                }

                return string.IsNullOrWhiteSpace(team) ? email : team;
            }
        }
    }

    public sealed class DigitalOceanTeam
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public sealed class DigitalOceanOAuthToken
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("info")]
        public DigitalOceanOAuthInfo? Info { get; set; }
    }

    public sealed class DigitalOceanOAuthInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }

    public sealed class DigitalOceanFirewallInboundRule
    {
        public string Protocol { get; set; } = "tcp";

        public string Ports { get; set; } = string.Empty;

        public List<string> SourceAddresses { get; set; } = [];
    }

    public sealed class DigitalOceanFirewall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("droplet_ids")]
        public List<long> DropletIds { get; set; } = [];

        [JsonPropertyName("inbound_rules")]
        public List<DigitalOceanFirewallInboundRuleDto> InboundRuleDtos { get; set; } = [];

        public IReadOnlyList<DigitalOceanFirewallInboundRule> InboundRules =>
            InboundRuleDtos.Select(rule => new DigitalOceanFirewallInboundRule
            {
                Protocol = rule.Protocol,
                Ports = rule.Ports ?? string.Empty,
                SourceAddresses = rule.Sources?.Addresses ?? [],
            }).ToList();
    }

    public sealed class DigitalOceanFirewallInboundRuleDto
    {
        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "tcp";

        [JsonPropertyName("ports")]
        public string? Ports { get; set; }

        [JsonPropertyName("sources")]
        public DigitalOceanFirewallAddresses? Sources { get; set; }
    }

    private sealed class DigitalOceanAccountResponse
    {
        [JsonPropertyName("account")]
        public DigitalOceanAccount? Account { get; set; }
    }

    private sealed class DigitalOceanFirewallListResponse
    {
        [JsonPropertyName("firewalls")]
        public List<DigitalOceanFirewall> Firewalls { get; set; } = [];

        [JsonPropertyName("meta")]
        public DigitalOceanMeta? Meta { get; set; }
    }

    private sealed class DigitalOceanFirewallResponse
    {
        [JsonPropertyName("firewall")]
        public DigitalOceanFirewall? Firewall { get; set; }
    }

    private sealed class DigitalOceanFirewallWriteRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("inbound_rules")]
        public List<DigitalOceanFirewallApiInbound> InboundRules { get; set; } = [];

        [JsonPropertyName("outbound_rules")]
        public List<DigitalOceanFirewallApiOutbound> OutboundRules { get; set; } = [];

        [JsonPropertyName("droplet_ids")]
        public List<long> DropletIds { get; set; } = [];
    }

    private sealed class DigitalOceanFirewallApiInbound
    {
        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "tcp";

        [JsonPropertyName("ports")]
        public string Ports { get; set; } = string.Empty;

        [JsonPropertyName("sources")]
        public DigitalOceanFirewallAddresses Sources { get; set; } = new();
    }

    private sealed class DigitalOceanFirewallApiOutbound
    {
        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "tcp";

        [JsonPropertyName("ports")]
        public string? Ports { get; set; }

        [JsonPropertyName("destinations")]
        public DigitalOceanFirewallAddresses Destinations { get; set; } = new();
    }

    public sealed class DigitalOceanFirewallAddresses
    {
        [JsonPropertyName("addresses")]
        public List<string> Addresses { get; set; } = [];
    }
}
