using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Resources;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class AzureComputeClient
{
    public const string ArmScope = "https://management.azure.com/.default";

    public const string OAuthScopes = "openid profile offline_access https://management.azure.com/.default";

    public const string DefaultTenant = "organizations";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public AzureComputeClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public static string ResolveTenantId(string? configuredTenantId)
    {
        var tenant = (configuredTenantId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(tenant) ? DefaultTenant : tenant;
    }

    public static string AuthorizeUrl(string tenantId)
        => $"https://login.microsoftonline.com/{Uri.EscapeDataString(ResolveTenantId(tenantId))}/oauth2/v2.0/authorize";

    public static string TokenUrl(string tenantId)
        => $"https://login.microsoftonline.com/{Uri.EscapeDataString(ResolveTenantId(tenantId))}/oauth2/v2.0/token";

    public static string DeviceCodeUrl(string tenantId)
        => $"https://login.microsoftonline.com/{Uri.EscapeDataString(ResolveTenantId(tenantId))}/oauth2/v2.0/devicecode";

    public static AzureAccess FromServicePrincipal(AzureCredentials credentials)
    {
        var tenantId = credentials.TenantId.Trim();
        var clientId = credentials.ClientId.Trim();
        var clientSecret = credentials.ClientSecret.Trim();
        var subscriptionId = credentials.SubscriptionId.Trim();
        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret)
            || string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new InvalidOperationException("Stored Azure credentials are incomplete.");
        }

        return new AzureAccess
        {
            Credential = new ClientSecretCredential(tenantId, clientId, clientSecret),
            SubscriptionId = subscriptionId,
            TenantId = tenantId,
        };
    }

    public static AzureAccess FromAccessToken(
        string accessToken,
        DateTimeOffset expiresOn,
        string? subscriptionId,
        string? tenantId)
        => new()
        {
            Credential = new AzureAccessTokenCredential(accessToken, expiresOn),
            SubscriptionId = (subscriptionId ?? string.Empty).Trim(),
            TenantId = ResolveTenantId(tenantId),
            AccessToken = accessToken.Trim(),
        };

    public Task ValidateCredentialsAsync(AzureCredentials credentials, CancellationToken cancellationToken)
        => ValidateAccessAsync(FromServicePrincipal(credentials), cancellationToken);

    public async Task ValidateAccessAsync(AzureAccess access, CancellationToken cancellationToken)
    {
        RequireSubscription(access);
        var client = CreateArmClient(access);
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(access.SubscriptionId));
        try
        {
            await subscription.GetAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Azure rejected the credentials or this identity cannot read the selected subscription.");
        }
    }

    public async Task<IReadOnlyList<AzureCatalogOption>> ListSubscriptionsAsync(
        AzureAccess access,
        CancellationToken cancellationToken)
    {
        var client = new ArmClient(access.Credential);
        var subscriptions = new List<AzureCatalogOption>();
        await foreach (var subscription in client.GetSubscriptions().GetAllAsync(cancellationToken))
        {
            var id = subscription.Data.SubscriptionId;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = (subscription.Data.DisplayName ?? string.Empty).Trim();
            subscriptions.Add(new AzureCatalogOption
            {
                Value = id,
                Label = string.IsNullOrWhiteSpace(name) || string.Equals(name, id, StringComparison.Ordinal)
                    ? id
                    : $"{name} ({id})",
                Description = id,
            });
        }

        return subscriptions
            .OrderBy(subscription => subscription.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<AzureLocationOption>> ListLocationsAsync(
        AzureAccess access,
        CancellationToken cancellationToken)
    {
        RequireSubscription(access);
        var client = CreateArmClient(access);
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(access.SubscriptionId));
        var locations = new List<AzureLocationOption>();

        await foreach (var location in subscription.GetLocationsAsync().WithCancellation(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(location.Name))
            {
                continue;
            }

            locations.Add(new AzureLocationOption
            {
                Value = location.Name,
                Label = string.IsNullOrWhiteSpace(location.DisplayName)
                    ? location.Name
                    : $"{location.DisplayName} ({location.Name})",
            });
        }

        return locations
            .OrderBy(location => location.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<AzureVmInstance>> ListRunningInstancesAsync(
        AzureAccess access,
        string? locationFilter,
        CancellationToken cancellationToken)
    {
        RequireSubscription(access);
        var client = CreateArmClient(access);
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(access.SubscriptionId));
        var filter = (locationFilter ?? string.Empty).Trim();
        var instances = new List<AzureVmInstance>();

        await foreach (var vm in subscription.GetVirtualMachinesAsync().WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(filter)
                && !string.Equals(vm.Data.Location.ToString(), filter, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(vm.Data.Location.Name, filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var instanceView = await vm.InstanceViewAsync(cancellationToken: cancellationToken);
            var powerState = instanceView.Value.Statuses?
                .FirstOrDefault(status => status.Code?.StartsWith("PowerState/", StringComparison.Ordinal) == true)
                ?.Code;

            if (!string.Equals(powerState, "PowerState/running", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsWindowsVm(vm))
            {
                continue;
            }

            var publicHost = await TryGetPublicIpAsync(client, vm, cancellationToken);
            if (string.IsNullOrWhiteSpace(publicHost))
            {
                continue;
            }

            instances.Add(ToVmInstance(vm, publicHost));
        }

        return instances
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AzureVmInstance?> FindInstanceAsync(
        AzureAccess access,
        string? instanceId,
        string? publicHost,
        CancellationToken cancellationToken)
    {
        RequireSubscription(access);
        var id = (instanceId ?? string.Empty).Trim();
        var host = (publicHost ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                var client = CreateArmClient(access);
                var vm = client.GetVirtualMachineResource(new ResourceIdentifier(id));
                var response = await vm.GetAsync(cancellationToken: cancellationToken);
                var resolvedHost = await TryGetPublicIpAsync(client, response.Value, cancellationToken)
                                   ?? string.Empty;
                return ToVmInstance(response.Value, resolvedHost);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through to public-IP search.
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var running = await ListRunningInstancesAsync(access, locationFilter: null, cancellationToken);
        return running.FirstOrDefault(instance =>
            string.Equals(instance.PublicHost, host, StringComparison.OrdinalIgnoreCase));
    }

    public async Task RunBootstrapScriptAsync(
        AzureAccess access,
        string vmResourceId,
        string script,
        CancellationToken cancellationToken)
    {
        var resourceId = (vmResourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new ArgumentException("Azure VM resource id is required.");
        }

        RequireSubscription(access);
        var client = CreateArmClient(access);
        var vm = client.GetVirtualMachineResource(new ResourceIdentifier(resourceId));
        var input = new RunCommandInput("RunShellScript")
        {
            Script = { script },
        };

        await vm.RunCommandAsync(WaitUntil.Completed, input, cancellationToken);
    }

    public async Task<(int Applied, string NsgId)> ApplyNsgRulesAsync(
        AzureAccess access,
        string vmResourceId,
        IReadOnlyList<AzureNsgInboundRule> rules,
        CancellationToken cancellationToken)
    {
        RequireSubscription(access);
        var client = CreateArmClient(access);
        var vm = client.GetVirtualMachineResource(new ResourceIdentifier(vmResourceId.Trim()));
        var vmResponse = await vm.GetAsync(cancellationToken: cancellationToken);
        var nic = await GetPrimaryNicAsync(client, vmResponse.Value, cancellationToken)
                  ?? throw new InvalidOperationException("Azure VM has no network interface to attach an NSG.");

        var nicData = nic.Data;
        NetworkSecurityGroupResource nsg;
        if (nicData.NetworkSecurityGroup?.Id is { } existingNsgId)
        {
            nsg = client.GetNetworkSecurityGroupResource(existingNsgId);
            await nsg.GetAsync(expand: null, cancellationToken);
        }
        else
        {
            var resourceGroupName = nic.Id.ResourceGroupName
                                    ?? vm.Id.ResourceGroupName
                                    ?? throw new InvalidOperationException("Could not resolve the Azure resource group for NSG create.");
            var location = nicData.Location;
            var nsgName = BuildNsgName(vmResponse.Value.Data.Name);
            var rg = client.GetResourceGroupResource(
                ResourceGroupResource.CreateResourceIdentifier(access.SubscriptionId, resourceGroupName));
            var created = await rg.GetNetworkSecurityGroups().CreateOrUpdateAsync(
                WaitUntil.Completed,
                nsgName,
                new NetworkSecurityGroupData { Location = location },
                cancellationToken: cancellationToken);
            nsg = created.Value;

            nicData.NetworkSecurityGroup = ArmNetworkModelFactory.NetworkSecurityGroupData(id: nsg.Id);
            await rg.GetNetworkInterfaces().CreateOrUpdateAsync(
                WaitUntil.Completed,
                nic.Data.Name,
                nicData,
                cancellationToken);
        }

        var applied = 0;
        var priority = 1000;
        foreach (var rule in rules)
        {
            var ruleName = BuildNsgRuleName(rule.Port);
            var data = new SecurityRuleData
            {
                Protocol = SecurityRuleProtocol.Tcp,
                Access = SecurityRuleAccess.Allow,
                Direction = SecurityRuleDirection.Inbound,
                Priority = priority,
                SourcePortRange = "*",
                DestinationPortRange = rule.Port.ToString(),
                SourceAddressPrefix = rule.SourceCidr,
                DestinationAddressPrefix = "*",
                Description = string.IsNullOrWhiteSpace(rule.Description)
                    ? $"Azeroth Platform tcp/{rule.Port}"
                    : rule.Description.Trim(),
            };

            await nsg.GetSecurityRules().CreateOrUpdateAsync(
                WaitUntil.Completed,
                ruleName,
                data,
                cancellationToken: cancellationToken);
            applied += 1;
            priority += 10;
        }

        return (applied, nsg.Id.ToString());
    }

    public async Task<IReadOnlyList<AzureNsgProbeRule>> ListNsgInboundRulesAsync(
        AzureAccess access,
        string vmResourceId,
        CancellationToken cancellationToken)
    {
        RequireSubscription(access);
        var client = CreateArmClient(access);
        var vm = client.GetVirtualMachineResource(new ResourceIdentifier(vmResourceId.Trim()));
        var vmResponse = await vm.GetAsync(cancellationToken: cancellationToken);
        var nic = await GetPrimaryNicAsync(client, vmResponse.Value, cancellationToken);
        if (nic?.Data.NetworkSecurityGroup?.Id is null)
        {
            return [];
        }

        var nsg = client.GetNetworkSecurityGroupResource(nic.Data.NetworkSecurityGroup.Id);
        var nsgData = (await nsg.GetAsync(expand: null, cancellationToken)).Value.Data;
        var rules = new List<AzureNsgProbeRule>();
        foreach (var rule in nsgData.SecurityRules)
        {
            if (rule.Direction != SecurityRuleDirection.Inbound)
            {
                continue;
            }

            rules.Add(new AzureNsgProbeRule
            {
                Name = rule.Name ?? string.Empty,
                Protocol = rule.Protocol?.ToString() ?? "Tcp",
                DestinationPortRange = rule.DestinationPortRange ?? string.Join(',', rule.DestinationPortRanges),
                SourceAddressPrefix = rule.SourceAddressPrefix ?? string.Join(',', rule.SourceAddressPrefixes),
                Access = rule.Access?.ToString() ?? "Allow",
            });
        }

        return rules;
    }

    public async Task<AzureOAuthToken> ExchangeAuthorizationCodeAsync(
        string tenantId,
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
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
            ["code"] = code.Trim(),
            ["redirect_uri"] = redirectUri.Trim(),
            ["code_verifier"] = codeVerifier.Trim(),
            ["scope"] = OAuthScopes,
        };
        return await PostOAuthTokenAsync(TokenUrl(tenantId), form, cancellationToken);
    }

    public async Task<AzureOAuthToken> RefreshAccessTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
            ["refresh_token"] = refreshToken.Trim(),
            ["scope"] = ArmScope,
        };
        return await PostOAuthTokenAsync(TokenUrl(tenantId), form, cancellationToken);
    }

    public async Task<AzureDeviceCodeResult> StartDeviceCodeAsync(
        string tenantId,
        string clientId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl(tenantId))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["scope"] = OAuthScopes,
            }),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseHttpError(body, "Azure device-code request failed."));
        }

        var payload = JsonSerializer.Deserialize<AzureDeviceCodeResult>(body, JsonOptions)
                      ?? throw new InvalidOperationException("Azure returned an invalid device-code response.");
        if (string.IsNullOrWhiteSpace(payload.DeviceCode) || string.IsNullOrWhiteSpace(payload.UserCode))
        {
            throw new InvalidOperationException("Azure did not return a device code.");
        }

        return payload;
    }

    public async Task<AzureOAuthToken> PollDeviceCodeAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId.Trim(),
            ["device_code"] = deviceCode.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            form["client_secret"] = clientSecret.Trim();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl(tenantId))
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var token = JsonSerializer.Deserialize<AzureOAuthToken>(body, JsonOptions)
                        ?? throw new InvalidOperationException("Azure returned an invalid OAuth token response.");
            if (string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new InvalidOperationException("Azure did not return an access token.");
            }

            return token;
        }

        var error = ParseOAuthErrorCode(body);
        if (string.Equals(error, "authorization_pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(error, "slow_down", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("authorization_pending");
        }

        throw new InvalidOperationException(ParseHttpError(body, "Azure device login failed."));
    }

    internal static bool NsgRuleCovers(AzureNsgProbeRule rule, int port, string expectedCidr)
    {
        if (!string.Equals(rule.Access, "Allow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(rule.Protocol, "Tcp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rule.Protocol, "*", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rule.Protocol, "All", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!PortSpecCovers(rule.DestinationPortRange, port))
        {
            return false;
        }

        var expected = (expectedCidr ?? string.Empty).Trim();
        var actual = (rule.SourceAddressPrefix ?? string.Empty).Trim();
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
               || actual is "*" or "0.0.0.0/0" or "Internet" or "Any";
    }

    internal static bool NsgRuleOpensPortPublicly(AzureNsgProbeRule rule, int port)
        => string.Equals(rule.Access, "Allow", StringComparison.OrdinalIgnoreCase)
           && PortSpecCovers(rule.DestinationPortRange, port)
           && (rule.SourceAddressPrefix is "*" or "0.0.0.0/0" or "Internet" or "Any");

    internal static string BuildNsgName(string vmName)
    {
        var fragment = SanitizeAzureName(vmName);
        var name = $"azp-{fragment}";
        return name.Length <= 80 ? name : name[..80].Trim('-');
    }

    internal static string BuildNsgRuleName(int port) => $"azp-tcp-{port}";

    internal static string? HintFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var json = EncodingUtf8FromBase64Url(parts[1]);
            using var document = JsonDocument.Parse(json);
            foreach (var key in new[] { "preferred_username", "upn", "email", "unique_name", "oid" })
            {
                if (document.RootElement.TryGetProperty(key, out var value)
                    && value.GetString() is { Length: > 0 } text)
                {
                    return text.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }

        return null;
    }

    private static AzureVmInstance ToVmInstance(VirtualMachineResource vm, string publicHost)
    {
        var imageOffer = vm.Data.StorageProfile?.ImageReference?.Offer ?? string.Empty;
        var imagePublisher = vm.Data.StorageProfile?.ImageReference?.Publisher ?? string.Empty;
        var imageSku = vm.Data.StorageProfile?.ImageReference?.Sku ?? string.Empty;
        var image = string.Join(' ',
            new[] { imagePublisher, imageOffer, imageSku }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var vmSize = vm.Data.HardwareProfile?.VmSize.ToString() ?? string.Empty;

        return new AzureVmInstance
        {
            Id = vm.Id.ToString(),
            Name = vm.Data.Name,
            Location = vm.Data.Location.ToString(),
            ResourceGroup = vm.Id.ResourceGroupName ?? string.Empty,
            PublicHost = publicHost,
            Image = image,
            VmSize = vmSize,
            SuggestedSshUser = SuggestSshUserFromImage(imageOffer, imagePublisher, imageSku),
        };
    }

    private static async Task<NetworkInterfaceResource?> GetPrimaryNicAsync(
        ArmClient client,
        VirtualMachineResource vm,
        CancellationToken cancellationToken)
    {
        var networkInterfaces = vm.Data.NetworkProfile?.NetworkInterfaces;
        if (networkInterfaces is null || networkInterfaces.Count == 0)
        {
            return null;
        }

        var primary = networkInterfaces.FirstOrDefault(item => item.Primary == true)
                      ?? networkInterfaces[0];
        if (string.IsNullOrWhiteSpace(primary.Id?.ToString()))
        {
            return null;
        }

        var nic = client.GetNetworkInterfaceResource(primary.Id);
        var response = await nic.GetAsync(cancellationToken: cancellationToken);
        return response.Value;
    }

    private static ArmClient CreateArmClient(AzureAccess access)
        => new(access.Credential, access.SubscriptionId);

    private static void RequireSubscription(AzureAccess access)
    {
        if (string.IsNullOrWhiteSpace(access.SubscriptionId))
        {
            throw new InvalidOperationException(
                "Select an Azure subscription before listing or bootstrapping VMs.");
        }
    }

    private static bool IsWindowsVm(VirtualMachineResource vm)
    {
        if (vm.Data.OSProfile?.LinuxConfiguration is not null)
        {
            return false;
        }

        if (vm.Data.OSProfile?.WindowsConfiguration is not null)
        {
            return true;
        }

        return vm.Data.StorageProfile?.OSDisk?.OSType == SupportedOperatingSystemType.Windows;
    }

    private static async Task<string?> TryGetPublicIpAsync(
        ArmClient client,
        VirtualMachineResource vm,
        CancellationToken cancellationToken)
    {
        var networkInterfaces = vm.Data.NetworkProfile?.NetworkInterfaces;
        if (networkInterfaces is null || networkInterfaces.Count == 0)
        {
            return null;
        }

        foreach (var networkInterfaceReference in networkInterfaces)
        {
            if (string.IsNullOrWhiteSpace(networkInterfaceReference.Id))
            {
                continue;
            }

            var nic = client.GetNetworkInterfaceResource(new ResourceIdentifier(networkInterfaceReference.Id!));
            var nicData = await nic.GetAsync(cancellationToken: cancellationToken);
            foreach (var ipConfiguration in nicData.Value.Data.IPConfigurations)
            {
                var publicIpId = ipConfiguration.PublicIPAddress?.Id?.ToString();
                if (string.IsNullOrWhiteSpace(publicIpId))
                {
                    continue;
                }

                var publicIp = client.GetPublicIPAddressResource(new ResourceIdentifier(publicIpId));
                var publicIpData = await publicIp.GetAsync(cancellationToken: cancellationToken);
                var address = publicIpData.Value.Data.IPAddress;
                if (!string.IsNullOrWhiteSpace(address))
                {
                    return address;
                }
            }
        }

        return null;
    }

    private static string SuggestSshUserFromImage(string offer, string publisher, string sku)
    {
        var combined = $"{publisher} {offer} {sku}".ToLowerInvariant();
        if (combined.Contains("debian", StringComparison.Ordinal))
        {
            return "debian";
        }

        return "azureuser";
    }

    private static bool PortSpecCovers(string? ports, int port)
    {
        var spec = (ports ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(spec) || spec is "*" or "All")
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

    private static string SanitizeAzureName(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "nsg";
        }

        var chars = trimmed.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var sanitized = new string(chars).Trim('-');
        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "nsg" : sanitized;
    }

    private async Task<AzureOAuthToken> PostOAuthTokenAsync(
        string url,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseHttpError(body, "Azure OAuth token request failed."));
        }

        var token = JsonSerializer.Deserialize<AzureOAuthToken>(body, JsonOptions)
                    ?? throw new InvalidOperationException("Azure returned an invalid OAuth token response.");
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Azure did not return an access token.");
        }

        return token;
    }

    private static string ParseOAuthErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.GetString() is { Length: > 0 } code)
            {
                return code;
            }
        }
        catch (JsonException)
        {
            // Fall through.
        }

        return string.Empty;
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

            if (document.RootElement.TryGetProperty("error", out var error)
                && error.GetString() is { Length: > 0 } errorText)
            {
                return errorText;
            }
        }
        catch (JsonException)
        {
            // Fall through.
        }

        var trimmed = (body ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return fallback;
        }

        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }

    private static string EncodingUtf8FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private sealed class AzureAccessTokenCredential : TokenCredential
    {
        private readonly AccessToken _token;

        public AzureAccessTokenCredential(string token, DateTimeOffset expiresOn)
        {
            _token = new AccessToken(token.Trim(), expiresOn);
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => _token;

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
            => new(_token);
    }

    public sealed class AzureAccess
    {
        public required TokenCredential Credential { get; init; }

        public string SubscriptionId { get; init; } = string.Empty;

        public string TenantId { get; init; } = string.Empty;

        public string? AccessToken { get; init; }
    }

    public sealed class AzureOAuthToken
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }

    public sealed class AzureDeviceCodeResult
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = string.Empty;

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = string.Empty;

        [JsonPropertyName("verification_uri")]
        public string? VerificationUri { get; set; }

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }
    }

    public sealed class AzureCredentials
    {
        public string TenantId { get; init; } = string.Empty;

        public string ClientId { get; init; } = string.Empty;

        public string ClientSecret { get; init; } = string.Empty;

        public string SubscriptionId { get; init; } = string.Empty;
    }

    public sealed class AzureVmInstance
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Location { get; init; } = string.Empty;

        public string ResourceGroup { get; init; } = string.Empty;

        public string PublicHost { get; init; } = string.Empty;

        public string Image { get; init; } = string.Empty;

        public string VmSize { get; init; } = string.Empty;

        public string SuggestedSshUser { get; init; } = "azureuser";
    }

    public sealed class AzureLocationOption
    {
        public string Value { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;
    }

    public sealed class AzureCatalogOption
    {
        public string Value { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string? Description { get; init; }
    }

    public sealed class AzureNsgInboundRule
    {
        public int Port { get; init; }

        public string SourceCidr { get; init; } = "0.0.0.0/0";

        public string? Description { get; init; }
    }

    public sealed class AzureNsgProbeRule
    {
        public string Name { get; init; } = string.Empty;

        public string Protocol { get; init; } = "Tcp";

        public string DestinationPortRange { get; init; } = string.Empty;

        public string SourceAddressPrefix { get; init; } = string.Empty;

        public string Access { get; init; } = "Allow";
    }
}
