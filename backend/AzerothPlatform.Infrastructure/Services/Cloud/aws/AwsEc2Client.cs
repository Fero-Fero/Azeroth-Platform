using System.Text;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class AwsEc2Client
{
    public async Task ValidateCredentialsAsync(
        AwsRuntimeCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials, "us-east-1");
        try
        {
            await client.DescribeRegionsAsync(new DescribeRegionsRequest(), cancellationToken);
        }
        catch (AmazonEC2Exception ex)
        {
            throw new InvalidOperationException(ParseAwsError(ex, "AWS rejected the access credentials."));
        }
        catch (AmazonServiceException ex)
        {
            throw new InvalidOperationException(ParseAwsError(ex, "AWS rejected the access credentials."));
        }
    }

    public async Task<IReadOnlyList<string>> ListRegionsAsync(
        AwsRuntimeCredentials credentials,
        CancellationToken cancellationToken)
        => await ResolveRegionsAsync(credentials, region: null, cancellationToken);

    public async Task<IReadOnlyList<AwsEc2Instance>> ListRunningInstancesAsync(
        AwsRuntimeCredentials credentials,
        string? region,
        CancellationToken cancellationToken)
    {
        var regions = await ResolveRegionsAsync(credentials, region, cancellationToken);
        var instances = new List<AwsEc2Instance>();

        foreach (var regionName in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            instances.AddRange(await ListRunningInstancesInRegionAsync(
                credentials,
                regionName,
                cancellationToken));
        }

        return instances
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task TerminateInstanceAsync(
        AwsRuntimeCredentials credentials,
        string region,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var id = (instanceId ?? string.Empty).Trim();
        var regionName = (region ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Instance id is required.");
        }

        if (string.IsNullOrWhiteSpace(regionName))
        {
            throw new ArgumentException("AWS region is required to terminate an instance.");
        }

        using var client = CreateClient(credentials, regionName);
        try
        {
            await client.TerminateInstancesAsync(
                new TerminateInstancesRequest { InstanceIds = [id] },
                cancellationToken);
        }
        catch (AmazonEC2Exception ex) when (ex.ErrorCode is "InvalidInstanceID.NotFound")
        {
            // Already gone.
        }
        catch (AmazonEC2Exception ex)
        {
            throw new InvalidOperationException(ParseAwsError(ex, "AWS rejected the terminate request."));
        }
        catch (AmazonServiceException ex)
        {
            throw new InvalidOperationException(ParseAwsError(ex, "AWS rejected the terminate request."));
        }
    }

    public async Task<AwsInstanceNetworkTarget> ResolveInstanceForFirewallAsync(
        AwsRuntimeCredentials credentials,
        string publicHost,
        string? region,
        string? instanceId,
        CancellationToken cancellationToken)
    {
        var host = (publicHost ?? string.Empty).Trim();
        var id = (instanceId ?? string.Empty).Trim();
        var regionName = (region ?? string.Empty).Trim();

        var searchAllRegions = string.IsNullOrWhiteSpace(regionName);
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(regionName))
        {
            try
            {
                return await ResolveInstanceInRegionAsync(
                    credentials,
                    regionName,
                    id,
                    host,
                    cancellationToken);
            }
            catch (AmazonEC2Exception ex) when (ex.ErrorCode is "InvalidInstanceID.NotFound" or "InvalidInstanceID.Malformed")
            {
                if (string.IsNullOrWhiteSpace(host))
                {
                    throw new InvalidOperationException(
                        ParseAwsError(ex, $"EC2 instance {id} was not found in {regionName}."));
                }

                searchAllRegions = true;
            }
            catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(host))
            {
                searchAllRegions = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("AWS region is required when instance id is provided.");
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Public host or instance id is required.");
        }

        var regions = searchAllRegions
            ? await ResolveRegionsAsync(credentials, region: null, cancellationToken)
            : [regionName];

        foreach (var candidateRegion in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = await FindInstanceByPublicHostInRegionAsync(
                credentials,
                candidateRegion,
                host,
                cancellationToken);
            if (match is not null)
            {
                return match;
            }
        }

        throw new InvalidOperationException(
            $"No running EC2 instance with public address {host} was found in the linked AWS account.");
    }

    public async Task<(int Applied, int Skipped)> ApplySecurityGroupIngressRulesAsync(
        AwsRuntimeCredentials credentials,
        string region,
        IReadOnlyList<string> securityGroupIds,
        IReadOnlyList<AwsIngressRule> rules,
        CancellationToken cancellationToken)
    {
        if (securityGroupIds.Count == 0)
        {
            throw new InvalidOperationException("The EC2 instance has no security groups to update.");
        }

        using var client = CreateClient(credentials, region);
        var applied = 0;
        var skipped = 0;

        foreach (var groupId in securityGroupIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await client.AuthorizeSecurityGroupIngressAsync(new AuthorizeSecurityGroupIngressRequest
                    {
                        GroupId = groupId,
                        IpPermissions =
                        [
                            new IpPermission
                            {
                                IpProtocol = rule.Protocol,
                                FromPort = rule.Port,
                                ToPort = rule.Port,
                                Ipv4Ranges =
                                [
                                    new IpRange
                                    {
                                        CidrIp = rule.Cidr,
                                        Description = TruncateDescription(rule.Description),
                                    },
                                ],
                            },
                        ],
                    }, cancellationToken);
                    applied += 1;
                }
                catch (AmazonEC2Exception ex) when (ex.ErrorCode is "InvalidPermission.Duplicate")
                {
                    skipped += 1;
                }
            }
        }

        return (applied, skipped);
    }

    public async Task<IReadOnlyList<AwsIngressRule>> ListInstanceIngressRulesAsync(
        AwsRuntimeCredentials credentials,
        string publicHost,
        string? region,
        string? instanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = await ResolveInstanceForFirewallAsync(
                credentials,
                publicHost,
                region,
                instanceId,
                cancellationToken);
            if (target.SecurityGroupIds.Count == 0)
            {
                return [];
            }

            using var client = CreateClient(credentials, target.Region);
            var groups = await client.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest
            {
                GroupIds = [.. target.SecurityGroupIds],
            }, cancellationToken);

            var rules = new List<AwsIngressRule>();
            foreach (var group in groups.SecurityGroups)
            {
                foreach (var permission in group.IpPermissions)
                {
                    var port = permission.FromPort;
                    foreach (var range in permission.Ipv4Ranges)
                    {
                        if (string.IsNullOrWhiteSpace(range.CidrIp))
                        {
                            continue;
                        }

                        rules.Add(new AwsIngressRule
                        {
                            Port = port,
                            Protocol = permission.IpProtocol ?? "tcp",
                            Cidr = range.CidrIp,
                            Description = range.Description ?? string.Empty,
                        });
                    }
                }
            }

            return rules;
        }
        catch (AmazonEC2Exception ex)
        {
            throw new InvalidOperationException(ParseAwsError(ex, "AWS rejected the security group probe."));
        }
        catch (AmazonServiceException ex)
        {
            throw new InvalidOperationException(ParseAwsError(ex, "AWS rejected the security group probe."));
        }
    }

    private static async Task<AwsInstanceNetworkTarget> ResolveInstanceInRegionAsync(
        AwsRuntimeCredentials credentials,
        string region,
        string instanceId,
        string publicHost,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials, region);
        var response = await client.DescribeInstancesAsync(new DescribeInstancesRequest
        {
            InstanceIds = [instanceId],
        }, cancellationToken);

        var instance = response.Reservations
            .SelectMany(reservation => reservation.Instances)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"EC2 instance {instanceId} was not found in {region}.");

        return BuildInstanceNetworkTarget(instance, region, publicHost);
    }

    private static async Task<AwsInstanceNetworkTarget?> FindInstanceByPublicHostInRegionAsync(
        AwsRuntimeCredentials credentials,
        string region,
        string publicHost,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials, region);
        var reservations = new List<Reservation>();
        string? nextToken = null;

        do
        {
            var response = await client.DescribeInstancesAsync(new DescribeInstancesRequest
            {
                Filters =
                [
                    new Filter("instance-state-name", ["running"]),
                ],
                NextToken = nextToken,
            }, cancellationToken);

            reservations.AddRange(response.Reservations);
            nextToken = response.NextToken;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        foreach (var instance in reservations.SelectMany(reservation => reservation.Instances))
        {
            var ip = instance.PublicIpAddress;
            if (string.IsNullOrWhiteSpace(ip))
            {
                ip = instance.PublicDnsName;
            }

            if (string.Equals(ip, publicHost, StringComparison.OrdinalIgnoreCase))
            {
                return BuildInstanceNetworkTarget(instance, region, publicHost);
            }
        }

        return null;
    }

    private static AwsInstanceNetworkTarget BuildInstanceNetworkTarget(
        Instance instance,
        string region,
        string publicHost)
    {
        var groupIds = instance.SecurityGroups
            .Select(group => group.GroupId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolvedHost = instance.PublicIpAddress;
        if (string.IsNullOrWhiteSpace(resolvedHost))
        {
            resolvedHost = instance.PublicDnsName ?? publicHost;
        }

        return new AwsInstanceNetworkTarget
        {
            InstanceId = instance.InstanceId ?? string.Empty,
            Region = region,
            PublicHost = resolvedHost,
            SecurityGroupIds = groupIds,
        };
    }

    private static string TruncateDescription(string? description)
    {
        var text = ToAwsAscii(description);
        return text.Length <= 255 ? text : text[..255];
    }

    /// <summary>
    /// EC2 GroupDescription and rule descriptions reject non-ASCII (em dashes, smart quotes, etc.).
    /// </summary>
    private static string ToAwsAscii(string? value)
    {
        var text = (value ?? string.Empty)
            .Replace('\u2014', '-')
            .Replace('\u2013', '-')
            .Replace('\u2212', '-')
            .Replace('\u00B7', '-')
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Trim();

        if (text.All(static c => c <= 0x7F))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch <= 0x7F)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim();
    }

    private static async Task<IReadOnlyList<string>> ResolveRegionsAsync(
        AwsRuntimeCredentials credentials,
        string? region,
        CancellationToken cancellationToken)
    {
        var regionFilter = (region ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(regionFilter))
        {
            return [regionFilter];
        }

        using var client = CreateClient(credentials, "us-east-1");
        var response = await client.DescribeRegionsAsync(new DescribeRegionsRequest(), cancellationToken);
        return response.Regions
            .Select(entry => entry.RegionName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<AwsCatalogOption>> ListLaunchInstanceTypesAsync(
        AwsRuntimeCredentials credentials,
        string region,
        CancellationToken cancellationToken)
    {
        var regionName = (region ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(regionName))
        {
            throw new ArgumentException("AWS region is required to list instance types.");
        }

        using var client = CreateClient(credentials, regionName);
        var availabilityZone = await TryGetDefaultSubnetAvailabilityZoneAsync(client, cancellationToken);
        var offerings = await ListOfferedInstanceTypesAsync(
            client,
            regionName,
            availabilityZone,
            cancellationToken);
        var freeTierTypes = await ListFreeTierInstanceTypesAsync(client, cancellationToken);
        var available = AwsLaunchInstanceTypeCatalog.SelectAvailable(offerings, freeTierTypes);

        return available
            .Select(type => new AwsCatalogOption
            {
                Value = type.Type,
                Label = AwsLaunchInstanceTypeCatalog.FormatLabel(type),
                Description = type.Architecture,
                Vcpus = type.VCpus,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<AwsCatalogOption>> ListLaunchImagesAsync(
        AwsRuntimeCredentials credentials,
        string region,
        IReadOnlyCollection<string>? architectures,
        CancellationToken cancellationToken)
    {
        var regionName = (region ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(regionName))
        {
            throw new ArgumentException("AWS region is required to list AMIs.");
        }

        var requested = (architectures ?? [])
            .Where(architecture => !string.IsNullOrWhiteSpace(architecture))
            .Select(architecture => architecture.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requested.Count == 0)
        {
            requested.Add("x86_64");
        }

        using var client = CreateClient(credentials, regionName);
        var images = new List<AwsCatalogOption>();

        foreach (var architecture in requested)
        {
            var isArm = architecture.Contains("arm", StringComparison.OrdinalIgnoreCase);
            var ubuntuArch = isArm ? "arm64" : "amd64";
            var amazonArch = isArm ? "arm64" : "x86_64";

            var ubuntu = await FindLatestImageAsync(
                client,
                owners: ["099720109477"],
                namePattern: $"ubuntu/images/hvm-ssd/ubuntu-jammy-22.04-{ubuntuArch}-server-*",
                labelPrefix: "Ubuntu 22.04 LTS",
                architecture,
                cancellationToken);
            if (ubuntu is not null)
            {
                images.Add(ubuntu);
            }

            var ubuntu2404 = await FindLatestImageAsync(
                client,
                owners: ["099720109477"],
                namePattern: $"ubuntu/images/hvm-ssd-gp3/ubuntu-noble-24.04-{ubuntuArch}-server-*",
                labelPrefix: "Ubuntu 24.04 LTS",
                architecture,
                cancellationToken);
            if (ubuntu2404 is not null)
            {
                images.Add(ubuntu2404);
            }

            var amazonLinux = await FindLatestImageAsync(
                client,
                owners: ["amazon"],
                namePattern: $"al2023-ami-2023*-kernel-*-{amazonArch}",
                labelPrefix: "Amazon Linux 2023",
                architecture,
                cancellationToken);
            if (amazonLinux is not null)
            {
                images.Add(amazonLinux);
            }
        }

        return images;
    }

    public async Task<AwsEc2Instance> CreateInstanceAsync(
        AwsRuntimeCredentials credentials,
        string region,
        string name,
        string instanceType,
        string imageId,
        string userDataScript,
        string keyPairName,
        string publicKeyMaterial,
        CancellationToken cancellationToken,
        string? adminSourceCidr = null,
        bool applyNetworkProfile = true,
        int? diskSizeGb = null)
    {
        var regionName = (region ?? string.Empty).Trim();
        var amiId = (imageId ?? string.Empty).Trim();
        var type = (instanceType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(regionName)
            || string.IsNullOrWhiteSpace(amiId)
            || string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("AWS region, AMI, and instance type are required.");
        }

        try
        {
            using var client = CreateClient(credentials, regionName);
            await ImportKeyPairAsync(client, keyPairName, publicKeyMaterial, cancellationToken);

            var network = await ResolveDefaultLaunchNetworkAsync(
                client,
                regionName,
                adminSourceCidr,
                applyNetworkProfile,
                cancellationToken);
            var offeredZones = await ListAvailabilityZonesForInstanceTypeAsync(client, type, cancellationToken);
            var subnets = network.Subnets
                .OrderByDescending(subnet => offeredZones.Contains(subnet.AvailabilityZone))
                .ThenByDescending(subnet => subnet.MapPublicIpOnLaunch)
                .ThenBy(subnet => subnet.AvailabilityZone, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var userData = Convert.ToBase64String(Encoding.UTF8.GetBytes(userDataScript));
            var rootVolume = await ResolveRootBlockDeviceAsync(client, amiId, diskSizeGb, cancellationToken);
            AmazonEC2Exception? lastAzError = null;

            for (var index = 0; index < subnets.Count; index += 1)
            {
                var subnet = subnets[index];
                try
                {
                    var runResponse = await client.RunInstancesAsync(
                        BuildRunInstancesRequest(
                            amiId,
                            type,
                            keyPairName,
                            userData,
                            name,
                            subnet.SubnetId,
                            network.SecurityGroupId,
                            rootVolume),
                        cancellationToken);

                    var instance = runResponse.Reservation?.Instances.FirstOrDefault()
                                   ?? throw new InvalidOperationException("AWS did not return a created instance.");

                    var instanceId = instance.InstanceId
                                     ?? throw new InvalidOperationException("AWS did not return an instance id.");

                    return await WaitForRunningInstanceAsync(
                        credentials,
                        regionName,
                        instanceId,
                        amiId,
                        cancellationToken);
                }
                catch (AmazonEC2Exception ex) when (
                    index < subnets.Count - 1 && IsRetryableLaunchPlacementError(ex))
                {
                    lastAzError = ex;
                }
            }

            if (lastAzError is not null)
            {
                throw lastAzError;
            }

            throw new InvalidOperationException($"Could not launch {type} in the default VPC for {regionName}.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is AmazonEC2Exception or AmazonServiceException)
        {
            throw new InvalidOperationException(ParseAwsError(ex, "AWS rejected the launch request."), ex);
        }
    }

    private static RunInstancesRequest BuildRunInstancesRequest(
        string imageId,
        string instanceType,
        string keyPairName,
        string userData,
        string name,
        string subnetId,
        string securityGroupId,
        BlockDeviceMapping? rootVolume)
    {
        var request = new RunInstancesRequest
        {
            ImageId = imageId,
            InstanceType = instanceType,
            MinCount = 1,
            MaxCount = 1,
            KeyName = keyPairName,
            UserData = userData,
            NetworkInterfaces =
            [
                new InstanceNetworkInterfaceSpecification
                {
                    DeviceIndex = 0,
                    SubnetId = subnetId,
                    Groups = [securityGroupId],
                    AssociatePublicIpAddress = true,
                },
            ],
            TagSpecifications =
            [
                new TagSpecification
                {
                    ResourceType = ResourceType.Instance,
                    Tags =
                    [
                        new Tag("Name", name),
                        new Tag("azeroth-platform", "launch"),
                    ],
                },
            ],
        };

        if (rootVolume is not null)
        {
            request.BlockDeviceMappings = [rootVolume];
        }

        return request;
    }

    private static bool IsRetryableLaunchPlacementError(AmazonEC2Exception exception)
    {
        if (exception.ErrorCode is "Unsupported"
            or "InsufficientInstanceCapacity"
            or "InsufficientFreeAddressesInSubnet")
        {
            return true;
        }

        var message = exception.Message ?? string.Empty;
        return message.Contains("Availability Zone", StringComparison.OrdinalIgnoreCase)
               || message.Contains("not supported in your requested", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AwsEc2Instance> WaitForRunningInstanceAsync(
        AwsRuntimeCredentials credentials,
        string region,
        string instanceId,
        string? imageId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials, region);
        const int maxAttempts = 60;

        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await client.DescribeInstancesAsync(new DescribeInstancesRequest
            {
                InstanceIds = [instanceId],
            }, cancellationToken);

            var instance = response.Reservations
                .SelectMany(reservation => reservation.Instances)
                .FirstOrDefault();

            if (instance is null)
            {
                throw new InvalidOperationException($"AWS instance {instanceId} was not found.");
            }

            var state = instance.State?.Name?.Value ?? string.Empty;
            if (string.Equals(state, "terminated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "shutting-down", StringComparison.OrdinalIgnoreCase))
            {
                var reason = instance.StateReason?.Message ?? state;
                throw new InvalidOperationException(
                    $"AWS instance {instanceId} entered {state}: {reason}");
            }

            var publicHost = instance.PublicIpAddress;
            if (string.IsNullOrWhiteSpace(publicHost))
            {
                publicHost = instance.PublicDnsName ?? string.Empty;
            }

            if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(publicHost))
            {
                var resolvedImageId = instance.ImageId ?? imageId ?? string.Empty;
                var imageName = await ResolveImageNameAsync(client, resolvedImageId, cancellationToken);

                return new AwsEc2Instance
                {
                    Id = instance.InstanceId ?? instanceId,
                    Name = ResolveInstanceName(instance),
                    Region = region,
                    AvailabilityZone = instance.Placement?.AvailabilityZone ?? string.Empty,
                    InstanceType = instance.InstanceType?.Value ?? string.Empty,
                    State = state,
                    PublicHost = publicHost,
                    Image = string.IsNullOrWhiteSpace(imageName) ? resolvedImageId : imageName,
                    SuggestedSshUser = SuggestSshUser(imageName, resolvedImageId),
                };
            }

            if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase) && attempt >= 12)
            {
                throw new InvalidOperationException(
                    $"AWS instance {instanceId} is running but has no public IP. Enable auto-assign public IPv4 on the default subnet, or pick another region.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException("Timed out waiting for the AWS instance to become running.");
    }

    private static async Task ImportKeyPairAsync(
        IAmazonEC2 client,
        string keyPairName,
        string publicKeyMaterial,
        CancellationToken cancellationToken)
    {
        var encodedPublicKey = SshKeyMaterialHelper.ToAwsImportPublicKeyMaterial(publicKeyMaterial);
        try
        {
            await client.ImportKeyPairAsync(new ImportKeyPairRequest
            {
                KeyName = keyPairName,
                PublicKeyMaterial = encodedPublicKey,
            }, cancellationToken);
        }
        catch (AmazonEC2Exception ex) when (ex.ErrorCode is "InvalidKeyPair.Duplicate")
        {
            await client.DeleteKeyPairAsync(new DeleteKeyPairRequest { KeyName = keyPairName }, cancellationToken);
            await client.ImportKeyPairAsync(new ImportKeyPairRequest
            {
                KeyName = keyPairName,
                PublicKeyMaterial = encodedPublicKey,
            }, cancellationToken);
        }
    }

    private sealed class AwsLaunchNetwork
    {
        public required string SecurityGroupId { get; init; }

        public required IReadOnlyList<AwsLaunchSubnet> Subnets { get; init; }
    }

    private sealed class AwsLaunchSubnet
    {
        public required string SubnetId { get; init; }

        public required string AvailabilityZone { get; init; }

        public required bool MapPublicIpOnLaunch { get; init; }
    }

    private static async Task<AwsLaunchNetwork> ResolveDefaultLaunchNetworkAsync(
        IAmazonEC2 client,
        string region,
        string? adminSourceCidr,
        bool applyNetworkProfile,
        CancellationToken cancellationToken)
    {
        var vpcResponse = await client.DescribeVpcsAsync(new DescribeVpcsRequest
        {
            Filters =
            [
                new Filter("is-default", ["true"]),
            ],
        }, cancellationToken);

        var vpc = vpcResponse.Vpcs.FirstOrDefault()
                  ?? throw new InvalidOperationException(
                      $"No default VPC found in {region}. Create a default VPC or launch manually.");

        var subnetResponse = await client.DescribeSubnetsAsync(new DescribeSubnetsRequest
        {
            Filters =
            [
                new Filter("vpc-id", [vpc.VpcId]),
                new Filter("default-for-az", ["true"]),
            ],
        }, cancellationToken);

        var subnets = subnetResponse.Subnets
            .Where(subnet => !string.IsNullOrWhiteSpace(subnet.SubnetId))
            .Select(subnet => new AwsLaunchSubnet
            {
                SubnetId = subnet.SubnetId,
                AvailabilityZone = subnet.AvailabilityZone ?? string.Empty,
                MapPublicIpOnLaunch = subnet.MapPublicIpOnLaunch,
            })
            .ToList();

        if (subnets.Count == 0)
        {
            throw new InvalidOperationException(
                $"No default subnet found in the default VPC for {region}.");
        }

        var securityGroupId = await ResolveLaunchSecurityGroupAsync(
            client,
            vpc.VpcId,
            adminSourceCidr,
            applyNetworkProfile,
            cancellationToken);
        return new AwsLaunchNetwork
        {
            SecurityGroupId = securityGroupId,
            Subnets = subnets,
        };
    }

    private static async Task<HashSet<string>> ListAvailabilityZonesForInstanceTypeAsync(
        IAmazonEC2 client,
        string instanceType,
        CancellationToken cancellationToken)
    {
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nextToken = null;

        do
        {
            var response = await client.DescribeInstanceTypeOfferingsAsync(new DescribeInstanceTypeOfferingsRequest
            {
                LocationType = LocationType.AvailabilityZone,
                Filters =
                [
                    new Filter("instance-type", [instanceType]),
                ],
                NextToken = nextToken,
            }, cancellationToken);

            foreach (var offering in response.InstanceTypeOfferings)
            {
                if (!string.IsNullOrWhiteSpace(offering.Location))
                {
                    zones.Add(offering.Location);
                }
            }

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        return zones;
    }

    private static async Task<string> ResolveLaunchSecurityGroupAsync(
        IAmazonEC2 client,
        string vpcId,
        string? adminSourceCidr,
        bool applyNetworkProfile,
        CancellationToken cancellationToken)
    {
        const string groupName = "azeroth-platform-launch";
        var existing = await client.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest
        {
            Filters =
            [
                new Filter("group-name", [groupName]),
                new Filter("vpc-id", [vpcId]),
            ],
        }, cancellationToken);

        var groupId = existing.SecurityGroups.FirstOrDefault()?.GroupId;
        if (string.IsNullOrWhiteSpace(groupId))
        {
            var created = await client.CreateSecurityGroupAsync(new CreateSecurityGroupRequest
            {
                GroupName = groupName,
                Description = ToAwsAscii("Azeroth Platform launch - SSH, game, and web ingress"),
                VpcId = vpcId,
            }, cancellationToken);
            groupId = created.GroupId;
        }

        var rules = applyNetworkProfile
            ? VpcSecurityCatalog.BuildLaunchCloudIngressRules(adminSourceCidr)
            :
            [
                new VpcSecurityRuleDto
                {
                    Port = 22,
                    Source = string.IsNullOrWhiteSpace(adminSourceCidr) ? "0.0.0.0/0" : adminSourceCidr.Trim(),
                    Description = "SSH for platform bootstrap",
                },
            ];

        foreach (var rule in rules)
        {
            var cidr = string.IsNullOrWhiteSpace(rule.Source) ? "0.0.0.0/0" : rule.Source.Trim();
            try
            {
                await client.AuthorizeSecurityGroupIngressAsync(new AuthorizeSecurityGroupIngressRequest
                {
                    GroupId = groupId,
                    IpPermissions =
                    [
                        new IpPermission
                        {
                            IpProtocol = "tcp",
                            FromPort = rule.Port,
                            ToPort = rule.Port,
                            Ipv4Ranges =
                            [
                                new IpRange
                                {
                                    CidrIp = cidr,
                                    Description = ToAwsAscii(rule.Description),
                                },
                            ],
                        },
                    ],
                }, cancellationToken);
            }
            catch (AmazonEC2Exception ex) when (ex.ErrorCode is "InvalidPermission.Duplicate")
            {
            }
        }

        return groupId;
    }

    private static async Task<IReadOnlyList<string>> ListOfferedInstanceTypesAsync(
        IAmazonEC2 client,
        string region,
        string? availabilityZone,
        CancellationToken cancellationToken)
    {
        var location = string.IsNullOrWhiteSpace(availabilityZone) ? region : availabilityZone;
        var locationType = string.IsNullOrWhiteSpace(availabilityZone)
            ? LocationType.Region
            : LocationType.AvailabilityZone;
        var offerings = new List<string>();
        string? nextToken = null;

        do
        {
            var response = await client.DescribeInstanceTypeOfferingsAsync(new DescribeInstanceTypeOfferingsRequest
            {
                LocationType = locationType,
                Filters =
                [
                    new Filter("location", [location]),
                ],
                NextToken = nextToken,
            }, cancellationToken);

            offerings.AddRange(response.InstanceTypeOfferings
                .Select(offering => offering.InstanceType?.Value ?? string.Empty)
                .Where(type => !string.IsNullOrWhiteSpace(type)));

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        return offerings
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<AwsLaunchInstanceTypeCatalog.LaunchType>> ListFreeTierInstanceTypesAsync(
        IAmazonEC2 client,
        CancellationToken cancellationToken)
    {
        try
        {
            var types = new List<AwsLaunchInstanceTypeCatalog.LaunchType>();
            string? nextToken = null;

            do
            {
                var response = await client.DescribeInstanceTypesAsync(new DescribeInstanceTypesRequest
                {
                    Filters =
                    [
                        new Filter("free-tier-eligible", ["true"]),
                    ],
                    NextToken = nextToken,
                }, cancellationToken);

                types.AddRange(response.InstanceTypes
                    .Select(ToLaunchType)
                    .Where(type => type is not null)
                    .Cast<AwsLaunchInstanceTypeCatalog.LaunchType>());

                nextToken = response.NextToken;
            }
            while (!string.IsNullOrWhiteSpace(nextToken));

            return types;
        }
        catch (AmazonEC2Exception)
        {
            return [];
        }
        catch (AmazonServiceException)
        {
            return [];
        }
    }

    private static async Task<BlockDeviceMapping?> ResolveRootBlockDeviceAsync(
        IAmazonEC2 client,
        string imageId,
        int? diskSizeGb,
        CancellationToken cancellationToken)
    {
        if (diskSizeGb is null or <= 0)
        {
            return null;
        }

        try
        {
            var response = await client.DescribeImagesAsync(new DescribeImagesRequest
            {
                ImageIds = [imageId],
            }, cancellationToken);

            var image = response.Images.FirstOrDefault();
            if (image is null)
            {
                return null;
            }

            var rootName = image.RootDeviceName;
            var mapping = image.BlockDeviceMappings
                .FirstOrDefault(entry =>
                    !string.IsNullOrWhiteSpace(rootName)
                    && string.Equals(entry.DeviceName, rootName, StringComparison.OrdinalIgnoreCase))
                ?? image.BlockDeviceMappings.FirstOrDefault(entry => entry.Ebs is not null);

            if (mapping?.Ebs is null || string.IsNullOrWhiteSpace(mapping.DeviceName))
            {
                return null;
            }

            var amiSize = mapping.Ebs.VolumeSize;
            mapping.Ebs.VolumeSize = Math.Max(diskSizeGb.Value, amiSize);
            mapping.Ebs.DeleteOnTermination = true;
            return mapping;
        }
        catch (AmazonEC2Exception)
        {
            return null;
        }
        catch (AmazonServiceException)
        {
            return null;
        }
    }

    private static AwsLaunchInstanceTypeCatalog.LaunchType? ToLaunchType(InstanceTypeInfo info)
    {
        var type = info.InstanceType?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var architecture = info.ProcessorInfo?.SupportedArchitectures?
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? "x86_64";
        if (info.ProcessorInfo?.SupportedArchitectures?.Any(entry =>
                string.Equals(entry, "x86_64", StringComparison.OrdinalIgnoreCase)) == true)
        {
            architecture = "x86_64";
        }

        return new AwsLaunchInstanceTypeCatalog.LaunchType(
            type,
            architecture,
            info.VCpuInfo?.DefaultVCpus ?? 0,
            (int)Math.Clamp(info.MemoryInfo?.SizeInMiB ?? 0, 0, int.MaxValue));
    }

    private static async Task<string?> TryGetDefaultSubnetAvailabilityZoneAsync(
        IAmazonEC2 client,
        CancellationToken cancellationToken)
    {
        try
        {
            var vpcResponse = await client.DescribeVpcsAsync(new DescribeVpcsRequest
            {
                Filters =
                [
                    new Filter("is-default", ["true"]),
                ],
            }, cancellationToken);

            var vpcId = vpcResponse.Vpcs.FirstOrDefault()?.VpcId;
            if (string.IsNullOrWhiteSpace(vpcId))
            {
                return null;
            }

            var subnetResponse = await client.DescribeSubnetsAsync(new DescribeSubnetsRequest
            {
                Filters =
                [
                    new Filter("vpc-id", [vpcId]),
                    new Filter("default-for-az", ["true"]),
                ],
            }, cancellationToken);

            return subnetResponse.Subnets.FirstOrDefault()?.AvailabilityZone;
        }
        catch (AmazonEC2Exception)
        {
            return null;
        }
        catch (AmazonServiceException)
        {
            return null;
        }
    }

    private static async Task<AwsCatalogOption?> FindLatestImageAsync(
        IAmazonEC2 client,
        IReadOnlyList<string> owners,
        string namePattern,
        string labelPrefix,
        string architecture,
        CancellationToken cancellationToken)
    {
        var arch = string.IsNullOrWhiteSpace(architecture) ? "x86_64" : architecture.Trim();
        var response = await client.DescribeImagesAsync(new DescribeImagesRequest
        {
            Owners = owners.ToList(),
            Filters =
            [
                new Filter("name", [namePattern]),
                new Filter("state", ["available"]),
                new Filter("architecture", [arch]),
                new Filter("virtualization-type", ["hvm"]),
            ],
        }, cancellationToken);

        var image = response.Images
            .OrderByDescending(entry => entry.CreationDate)
            .FirstOrDefault();

        if (image is null || string.IsNullOrWhiteSpace(image.ImageId))
        {
            return null;
        }

        return new AwsCatalogOption
        {
            Value = image.ImageId,
            Label = $"{labelPrefix} ({arch})",
            Description = arch,
        };
    }

    private static async Task<string> ResolveImageNameAsync(
        IAmazonEC2 client,
        string imageId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return string.Empty;
        }

        var response = await client.DescribeImagesAsync(new DescribeImagesRequest
        {
            ImageIds = [imageId],
        }, cancellationToken);

        var image = response.Images.FirstOrDefault();
        if (image is null)
        {
            return imageId;
        }

        return string.IsNullOrWhiteSpace(image.Name) ? imageId : image.Name;
    }

    private static async Task<IReadOnlyList<AwsEc2Instance>> ListRunningInstancesInRegionAsync(
        AwsRuntimeCredentials credentials,
        string region,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials, region);
        var reservations = new List<Reservation>();
        string? nextToken = null;

        do
        {
            var response = await client.DescribeInstancesAsync(new DescribeInstancesRequest
            {
                Filters =
                [
                    new Filter("instance-state-name", ["running"]),
                ],
                NextToken = nextToken,
            }, cancellationToken);

            reservations.AddRange(response.Reservations);
            nextToken = response.NextToken;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        var imageNames = await ResolveImageNamesAsync(client, reservations, cancellationToken);

        return reservations
            .SelectMany(reservation => reservation.Instances)
            .Select(instance =>
            {
                var imageId = instance.ImageId ?? string.Empty;
                imageNames.TryGetValue(imageId, out var imageName);
                var publicHost = instance.PublicIpAddress;
                if (string.IsNullOrWhiteSpace(publicHost))
                {
                    publicHost = instance.PublicDnsName ?? string.Empty;
                }

                return new AwsEc2Instance
                {
                    Id = instance.InstanceId ?? string.Empty,
                    Name = ResolveInstanceName(instance),
                    Region = region,
                    AvailabilityZone = instance.Placement?.AvailabilityZone ?? string.Empty,
                    InstanceType = instance.InstanceType?.Value ?? string.Empty,
                    State = instance.State?.Name?.Value ?? string.Empty,
                    PublicHost = publicHost,
                    Image = string.IsNullOrWhiteSpace(imageName) ? imageId : imageName,
                    SuggestedSshUser = SuggestSshUser(imageName, imageId),
                };
            })
            .Where(instance => !string.IsNullOrWhiteSpace(instance.PublicHost))
            .ToList();
    }

    private static async Task<Dictionary<string, string>> ResolveImageNamesAsync(
        IAmazonEC2 client,
        IReadOnlyList<Reservation> reservations,
        CancellationToken cancellationToken)
    {
        var imageIds = reservations
            .SelectMany(reservation => reservation.Instances)
            .Select(instance => instance.ImageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        if (imageIds.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var imageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in imageIds.Chunk(100))
        {
            var response = await client.DescribeImagesAsync(new DescribeImagesRequest
            {
                ImageIds = batch.ToList(),
            }, cancellationToken);

            foreach (var image in response.Images)
            {
                if (string.IsNullOrWhiteSpace(image.ImageId))
                {
                    continue;
                }

                var name = image.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = image.Description ?? image.ImageId;
                }

                imageNames[image.ImageId] = name;
            }
        }

        return imageNames;
    }

    internal static string SuggestSshUser(string? imageName, string? imageId)
    {
        var combined = $"{imageName} {imageId}".ToLowerInvariant();
        if (combined.Contains("windows", StringComparison.Ordinal))
        {
            return "Administrator";
        }

        if (combined.Contains("ubuntu", StringComparison.Ordinal))
        {
            return "ubuntu";
        }

        if (combined.Contains("debian", StringComparison.Ordinal))
        {
            return "debian";
        }

        if (combined.Contains("amzn", StringComparison.Ordinal)
            || combined.Contains("amazon-linux", StringComparison.Ordinal)
            || combined.Contains("amazon linux", StringComparison.Ordinal))
        {
            return "ec2-user";
        }

        return "ubuntu";
    }

    private static string ResolveInstanceName(Instance instance)
    {
        var nameTag = instance.Tags?
            .FirstOrDefault(tag => string.Equals(tag.Key, "Name", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(nameTag))
        {
            return nameTag;
        }

        return instance.InstanceId ?? string.Empty;
    }

    private static AmazonEC2Client CreateClient(AwsRuntimeCredentials credentials, string region)
    {
        var config = new AmazonEC2Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
        };
        return new AmazonEC2Client(credentials.ToSdk(), config);
    }

    private static string ParseAwsError(Exception exception, string fallback)
    {
        return exception.Message switch
        {
            { Length: > 0 } message when message.Length <= 400 => message,
            { Length: > 400 } message => message[..400] + "…",
            _ => fallback,
        };
    }

    public sealed class AwsEc2Instance
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Region { get; init; } = string.Empty;

        public string AvailabilityZone { get; init; } = string.Empty;

        public string InstanceType { get; init; } = string.Empty;

        public string State { get; init; } = string.Empty;

        public string PublicHost { get; init; } = string.Empty;

        public string Image { get; init; } = string.Empty;

        public string SuggestedSshUser { get; init; } = "ubuntu";
    }

    public sealed class AwsCatalogOption
    {
        public string Value { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string? Description { get; init; }

        public int? Vcpus { get; init; }
    }

    public sealed class AwsInstanceNetworkTarget
    {
        public string InstanceId { get; init; } = string.Empty;

        public string Region { get; init; } = string.Empty;

        public string PublicHost { get; init; } = string.Empty;

        public IReadOnlyList<string> SecurityGroupIds { get; init; } = [];
    }

    public sealed class AwsIngressRule
    {
        public int Port { get; init; }

        public string Protocol { get; init; } = "tcp";

        public string Cidr { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;
    }
}
