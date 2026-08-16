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
        _ = await GetAccountAsync(accessToken, cancellationToken);
    }

    public async Task<VultrAccount> GetAccountAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "account", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Vultr rejected the access token."));
        }

        var payload = JsonSerializer.Deserialize<VultrAccountResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Vultr returned an invalid account response.");
        return payload.Account ?? throw new InvalidOperationException("Vultr did not return an account.");
    }

    public async Task<string> ResolveAuthorizationEndpointAsync(
        string providerId,
        string? configuredAuthorizeUrl,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredAuthorizeUrl))
        {
            return configuredAuthorizeUrl.Trim();
        }

        var id = providerId.Trim();
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"oidc/provider/{Uri.EscapeDataString(id)}/.well-known/openid-configuration");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var discovery = JsonSerializer.Deserialize<VultrOidcDiscovery>(body, JsonOptions);
                if (!string.IsNullOrWhiteSpace(discovery?.AuthorizationEndpoint))
                {
                    return discovery.AuthorizationEndpoint.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to the provider-id fallback.
        }

        return $"https://api.vultr.com/v2/oidc/provider/{Uri.EscapeDataString(id)}/authorize";
    }

    public Task<VultrOAuthToken> ExchangeAuthorizationCodeAsync(
        string providerId,
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
            ["redirect_url"] = redirectUri.Trim(),
        };
        return PostOAuthTokenAsync(providerId, clientId, clientSecret, form, cancellationToken);
    }

    public Task<VultrOAuthToken> RefreshAccessTokenAsync(
        string providerId,
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken.Trim(),
        };
        return PostOAuthTokenAsync(providerId, clientId, clientSecret, form, cancellationToken);
    }

    public async Task<VultrInstance?> FindInstanceAsync(
        string accessToken,
        string? instanceId,
        string? publicHost,
        CancellationToken cancellationToken)
    {
        var id = (instanceId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return await GetInstanceAsync(accessToken, id, cancellationToken);
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

        var instances = await ListInstancesAsync(accessToken, regionFilter: null, cancellationToken);
        return instances.FirstOrDefault(instance =>
            string.Equals(instance.PublicHost, host, StringComparison.OrdinalIgnoreCase)
            || string.Equals(instance.Id, host, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<VultrFirewallGroup> ApplyFirewallGroupAsync(
        string accessToken,
        string description,
        string? instanceId,
        IReadOnlyList<VultrFirewallInboundRule> inboundRules,
        CancellationToken cancellationToken)
    {
        var groups = await ListFirewallGroupsAsync(accessToken, cancellationToken);
        var match = groups.FirstOrDefault(group =>
                        string.Equals(group.Description, description, StringComparison.OrdinalIgnoreCase));

        if (match is null && !string.IsNullOrWhiteSpace(instanceId))
        {
            var instance = await FindInstanceAsync(accessToken, instanceId, publicHost: null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(instance?.FirewallGroupId))
            {
                match = groups.FirstOrDefault(group =>
                    string.Equals(group.Id, instance.FirewallGroupId, StringComparison.OrdinalIgnoreCase)
                    && group.Description.StartsWith("azeroth-platform", StringComparison.OrdinalIgnoreCase));
            }
        }

        var group = match is null
            ? await CreateFirewallGroupAsync(accessToken, description, cancellationToken)
            : match;

        var existing = await ListFirewallRulesAsync(accessToken, group.Id, cancellationToken);
        foreach (var rule in inboundRules)
        {
            if (existing.Any(item => FirewallRuleCovers(item, rule)))
            {
                continue;
            }

            await CreateFirewallRuleAsync(accessToken, group.Id, rule, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            await AttachFirewallGroupAsync(accessToken, instanceId.Trim(), group.Id, cancellationToken);
        }

        return group;
    }

    public async Task<IReadOnlyList<VultrFirewallInboundRule>> ListInstanceFirewallRulesAsync(
        string accessToken,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await GetInstanceAsync(accessToken, instanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(instance.FirewallGroupId))
        {
            return [];
        }

        return await ListFirewallRulesAsync(accessToken, instance.FirewallGroupId, cancellationToken);
    }

    internal static bool FirewallRuleCovers(VultrFirewallInboundRule actual, int port, string expectedCidr)
    {
        if (!string.Equals(actual.Protocol, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!PortSpecCovers(actual.Port, port))
        {
            return false;
        }

        var actualCidr = ToCidr(actual.Subnet, actual.SubnetSize);
        var expected = (expectedCidr ?? string.Empty).Trim();
        return string.Equals(actualCidr, expected, StringComparison.OrdinalIgnoreCase)
               || actualCidr is "0.0.0.0/0" or "::/0";
    }

    internal static bool FirewallRuleOpensPortPublicly(VultrFirewallInboundRule rule, int port)
        => string.Equals(rule.Protocol, "tcp", StringComparison.OrdinalIgnoreCase)
           && PortSpecCovers(rule.Port, port)
           && ToCidr(rule.Subnet, rule.SubnetSize) is "0.0.0.0/0" or "::/0";

    internal static (string Subnet, int SubnetSize) SplitCidr(string cidr)
    {
        var value = (cidr ?? string.Empty).Trim();
        var slash = value.LastIndexOf('/');
        if (slash <= 0 || slash >= value.Length - 1)
        {
            return ("0.0.0.0", 0);
        }

        var subnet = value[..slash];
        return int.TryParse(value[(slash + 1)..], out var size)
            ? (subnet, size)
            : ("0.0.0.0", 0);
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
        string? firewallGroupId,
        CancellationToken cancellationToken)
    {
        object payload = string.IsNullOrWhiteSpace(firewallGroupId)
            ? new
            {
                region,
                plan,
                os_id = osId,
                label,
                user_data = userData,
                sshkey_id = sshKeyIds,
                tags = new[] { "azeroth-platform" },
            }
            : new
            {
                region,
                plan,
                os_id = osId,
                label,
                user_data = userData,
                sshkey_id = sshKeyIds,
                tags = new[] { "azeroth-platform" },
                firewall_group_id = firewallGroupId.Trim(),
            };

        var json = JsonSerializer.Serialize(payload);
        using var request = CreateRequest(HttpMethod.Post, "instances", accessToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
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

    public async Task DeleteInstanceAsync(
        string accessToken,
        string instanceId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"instances/{instanceId}", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseErrorMessage(body, "Failed to delete Vultr instance."));
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
            Plan = instance.Plan ?? string.Empty,
            FirewallGroupId = instance.FirewallGroupId ?? string.Empty,
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

    private async Task<VultrOAuthToken> PostOAuthTokenAsync(
        string providerId,
        string clientId,
        string clientSecret,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"oidc/provider/{Uri.EscapeDataString(providerId.Trim())}/token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId.Trim()}:{clientSecret.Trim()}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Vultr OAuth token exchange failed."));
        }

        var payload = JsonSerializer.Deserialize<VultrOAuthToken>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Vultr returned an invalid OAuth token response.");
        if (string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new InvalidOperationException("Vultr did not return an access token.");
        }

        return payload;
    }

    private async Task<IReadOnlyList<VultrFirewallGroup>> ListFirewallGroupsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var groups = new List<VultrFirewallGroup>();
        var cursor = string.Empty;
        while (true)
        {
            var path = string.IsNullOrWhiteSpace(cursor)
                ? "firewalls?per_page=100"
                : $"firewalls?per_page=100&cursor={Uri.EscapeDataString(cursor)}";
            using var request = CreateRequest(HttpMethod.Get, path, accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Vultr firewall groups."));
            }

            var payload = JsonSerializer.Deserialize<VultrFirewallGroupListResponse>(body, JsonOptions)
                          ?? new VultrFirewallGroupListResponse();
            groups.AddRange(payload.FirewallGroups);
            if (payload.Meta is null || string.IsNullOrWhiteSpace(payload.Meta.Next) || payload.FirewallGroups.Count == 0)
            {
                break;
            }

            cursor = payload.Meta.Next;
        }

        return groups;
    }

    private async Task<VultrFirewallGroup> CreateFirewallGroupAsync(
        string accessToken,
        string description,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { description });
        using var request = CreateRequest(HttpMethod.Post, "firewalls", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to create Vultr firewall group."));
        }

        var created = JsonSerializer.Deserialize<VultrFirewallGroupResponse>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Vultr returned an invalid firewall group response.");
        return created.FirewallGroup ?? throw new InvalidOperationException("Vultr did not return a firewall group.");
    }

    private async Task<IReadOnlyList<VultrFirewallInboundRule>> ListFirewallRulesAsync(
        string accessToken,
        string firewallGroupId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"firewalls/{firewallGroupId}/rules?per_page=200", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseErrorMessage(body, "Failed to list Vultr firewall rules."));
        }

        var payload = JsonSerializer.Deserialize<VultrFirewallRuleListResponse>(body, JsonOptions)
                      ?? new VultrFirewallRuleListResponse();
        return payload.FirewallRules;
    }

    private async Task CreateFirewallRuleAsync(
        string accessToken,
        string firewallGroupId,
        VultrFirewallInboundRule rule,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ip_type = "v4",
            protocol = string.IsNullOrWhiteSpace(rule.Protocol) ? "tcp" : rule.Protocol,
            subnet = rule.Subnet,
            subnet_size = rule.SubnetSize,
            port = rule.Port,
            notes = rule.Notes,
        });
        using var request = CreateRequest(HttpMethod.Post, $"firewalls/{firewallGroupId}/rules", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseErrorMessage(body, "Failed to create Vultr firewall rule."));
    }

    private async Task AttachFirewallGroupAsync(
        string accessToken,
        string instanceId,
        string firewallGroupId,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { firewall_group_id = firewallGroupId });
        using var request = CreateRequest(HttpMethod.Patch, $"instances/{instanceId}", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ParseErrorMessage(body, "Failed to attach Vultr firewall group."));
    }

    private static bool FirewallRuleCovers(VultrFirewallInboundRule actual, VultrFirewallInboundRule expected)
        => FirewallRuleCovers(actual, ParseSinglePort(expected.Port), ToCidr(expected.Subnet, expected.SubnetSize));

    private static int ParseSinglePort(string? port)
        => int.TryParse((port ?? string.Empty).Trim(), out var value) ? value : -1;

    private static bool PortSpecCovers(string? ports, int port)
    {
        var spec = (ports ?? string.Empty).Trim();
        if (int.TryParse(spec, out var single))
        {
            return single == port;
        }

        var separator = spec.IndexOf(':');
        if (separator < 0)
        {
            separator = spec.IndexOf('-');
        }

        if (separator <= 0 || separator >= spec.Length - 1)
        {
            return false;
        }

        return int.TryParse(spec[..separator], out var from)
               && int.TryParse(spec[(separator + 1)..], out var to)
               && port >= from
               && port <= to;
    }

    private static string ToCidr(string? subnet, int subnetSize)
    {
        var address = string.IsNullOrWhiteSpace(subnet) ? "0.0.0.0" : subnet.Trim();
        return $"{address}/{subnetSize}";
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

        public string Plan { get; init; } = string.Empty;

        public string FirewallGroupId { get; init; } = string.Empty;

        public string SuggestedSshUser { get; init; } = "root";
    }

    public sealed class VultrAccount
    {
        public string Name { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string DisplayHint
        {
            get
            {
                var email = Email.Trim();
                var name = Name.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(email))
                {
                    return $"{email} ({name})";
                }

                return string.IsNullOrWhiteSpace(email) ? name : email;
            }
        }
    }

    public sealed class VultrOAuthToken
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

    public sealed class VultrFirewallGroup
    {
        public string Id { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;
    }

    public sealed class VultrFirewallInboundRule
    {
        [JsonPropertyName("ip_type")]
        public string IpType { get; init; } = "v4";

        public string Protocol { get; init; } = "tcp";

        public string Port { get; init; } = string.Empty;

        public string Subnet { get; init; } = "0.0.0.0";

        [JsonPropertyName("subnet_size")]
        public int SubnetSize { get; init; }

        public string Notes { get; init; } = string.Empty;
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

        public string Plan { get; init; } = string.Empty;

        [JsonPropertyName("firewall_group_id")]
        public string FirewallGroupId { get; init; } = string.Empty;
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

    private sealed class VultrAccountResponse
    {
        public VultrAccount? Account { get; init; }
    }

    private sealed class VultrOidcDiscovery
    {
        [JsonPropertyName("authorization_endpoint")]
        public string? AuthorizationEndpoint { get; init; }
    }

    private sealed class VultrFirewallGroupListResponse
    {
        [JsonPropertyName("firewall_groups")]
        public List<VultrFirewallGroup> FirewallGroups { get; init; } = [];

        public VultrMeta? Meta { get; init; }
    }

    private sealed class VultrFirewallGroupResponse
    {
        [JsonPropertyName("firewall_group")]
        public VultrFirewallGroup? FirewallGroup { get; init; }
    }

    private sealed class VultrFirewallRuleListResponse
    {
        [JsonPropertyName("firewall_rules")]
        public List<VultrFirewallInboundRule> FirewallRules { get; init; } = [];
    }
}
