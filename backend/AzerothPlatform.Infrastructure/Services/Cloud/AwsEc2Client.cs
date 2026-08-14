using System.Text;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class AwsEc2Client
{
    private static readonly string[] PreferredInstanceTypePrefixes =
    [
        "t3.",
        "t3a.",
        "t2.",
        "m5.",
        "m6i.",
        "c5.",
        "c6i.",
    ];

    private static readonly HashSet<string> PreferredInstanceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "t3.micro",
        "t3.small",
        "t3.medium",
        "t3.large",
        "t2.micro",
        "t2.small",
        "m5.large",
        "c5.large",
    };

    public async Task ValidateCredentialsAsync(
        string accessKeyId,
        string secretAccessKey,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(accessKeyId, secretAccessKey, "us-east-1");
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
        string accessKeyId,
        string secretAccessKey,
        CancellationToken cancellationToken)
        => await ResolveRegionsAsync(accessKeyId, secretAccessKey, region: null, cancellationToken);

    public async Task<IReadOnlyList<AwsEc2Instance>> ListRunningInstancesAsync(
        string accessKeyId,
        string secretAccessKey,
        string? region,
        CancellationToken cancellationToken)
    {
        var regions = await ResolveRegionsAsync(accessKeyId, secretAccessKey, region, cancellationToken);
        var instances = new List<AwsEc2Instance>();

        foreach (var regionName in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            instances.AddRange(await ListRunningInstancesInRegionAsync(
                accessKeyId,
                secretAccessKey,
                regionName,
                cancellationToken));
        }

        return instances
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AwsInstanceNetworkTarget> ResolveInstanceForFirewallAsync(
        string accessKeyId,
        string secretAccessKey,
        string publicHost,
        string? region,
        string? instanceId,
        CancellationToken cancellationToken)
    {
        var host = (publicHost ?? string.Empty).Trim();
        var id = (instanceId ?? string.Empty).Trim();
        var regionName = (region ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(id))
        {
            if (string.IsNullOrWhiteSpace(regionName))
            {
                throw new ArgumentException("AWS region is required when instance id is provided.");
            }

            return await ResolveInstanceInRegionAsync(
                accessKeyId,
                secretAccessKey,
                regionName,
                id,
                host,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Public host or instance id is required.");
        }

        var regions = string.IsNullOrWhiteSpace(regionName)
            ? await ResolveRegionsAsync(accessKeyId, secretAccessKey, region: null, cancellationToken)
            : [regionName];

        foreach (var candidateRegion in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = await FindInstanceByPublicHostInRegionAsync(
                accessKeyId,
                secretAccessKey,
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
        string accessKeyId,
        string secretAccessKey,
        string region,
        IReadOnlyList<string> securityGroupIds,
        IReadOnlyList<AwsIngressRule> rules,
        CancellationToken cancellationToken)
    {
        if (securityGroupIds.Count == 0)
        {
            throw new InvalidOperationException("The EC2 instance has no security groups to update.");
        }

        using var client = CreateClient(accessKeyId, secretAccessKey, region);
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

    private static async Task<AwsInstanceNetworkTarget> ResolveInstanceInRegionAsync(
        string accessKeyId,
        string secretAccessKey,
        string region,
        string instanceId,
        string publicHost,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(accessKeyId, secretAccessKey, region);
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
        string accessKeyId,
        string secretAccessKey,
        string region,
        string publicHost,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(accessKeyId, secretAccessKey, region);
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
        var text = (description ?? string.Empty).Trim();
        if (text.Length <= 255)
        {
            return text;
        }

        return text[..255];
    }

    private static async Task<IReadOnlyList<string>> ResolveRegionsAsync(
        string accessKeyId,
        string secretAccessKey,
        string? region,
        CancellationToken cancellationToken)
    {
        var regionFilter = (region ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(regionFilter))
        {
            return [regionFilter];
        }

        using var client = CreateClient(accessKeyId, secretAccessKey, "us-east-1");
        var response = await client.DescribeRegionsAsync(new DescribeRegionsRequest(), cancellationToken);
        return response.Regions
            .Select(entry => entry.RegionName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<AwsCatalogOption>> ListLaunchInstanceTypesAsync(
        string accessKeyId,
        string secretAccessKey,
        string region,
        CancellationToken cancellationToken)
    {
        var regionName = (region ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(regionName))
        {
            throw new ArgumentException("AWS region is required to list instance types.");
        }

        using var client = CreateClient(accessKeyId, secretAccessKey, regionName);
        var offerings = new List<string>();
        string? nextToken = null;

        do
        {
            var response = await client.DescribeInstanceTypeOfferingsAsync(new DescribeInstanceTypeOfferingsRequest
            {
                LocationType = LocationType.Region,
                Filters =
                [
                    new Filter("location", [regionName]),
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
            .Where(IsPreferredLaunchInstanceType)
            .OrderBy(type => PreferredInstanceTypes.Contains(type) ? 0 : 1)
            .ThenBy(type => type, StringComparer.OrdinalIgnoreCase)
            .Select(type => new AwsCatalogOption
            {
                Value = type,
                Label = type,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<AwsCatalogOption>> ListLaunchImagesAsync(
        string accessKeyId,
        string secretAccessKey,
        string region,
        CancellationToken cancellationToken)
    {
        var regionName = (region ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(regionName))
        {
            throw new ArgumentException("AWS region is required to list AMIs.");
        }

        using var client = CreateClient(accessKeyId, secretAccessKey, regionName);
        var images = new List<AwsCatalogOption>();

        var ubuntu = await FindLatestImageAsync(
            client,
            owners: ["099720109477"],
            namePattern: "ubuntu/images/hvm-ssd/ubuntu-jammy-22.04-amd64-server-*",
            labelPrefix: "Ubuntu 22.04 LTS",
            cancellationToken);
        if (ubuntu is not null)
        {
            images.Add(ubuntu);
        }

        var ubuntu2404 = await FindLatestImageAsync(
            client,
            owners: ["099720109477"],
            namePattern: "ubuntu/images/hvm-ssd-gp3/ubuntu-noble-24.04-amd64-server-*",
            labelPrefix: "Ubuntu 24.04 LTS",
            cancellationToken);
        if (ubuntu2404 is not null)
        {
            images.Add(ubuntu2404);
        }

        var amazonLinux = await FindLatestImageAsync(
            client,
            owners: ["amazon"],
            namePattern: "al2023-ami-2023*-kernel-*-x86_64",
            labelPrefix: "Amazon Linux 2023",
            cancellationToken);
        if (amazonLinux is not null)
        {
            images.Add(amazonLinux);
        }

        return images;
    }

    public async Task<AwsEc2Instance> CreateInstanceAsync(
        string accessKeyId,
        string secretAccessKey,
        string region,
        string name,
        string instanceType,
        string imageId,
        string userDataScript,
        string keyPairName,
        string publicKeyMaterial,
        CancellationToken cancellationToken)
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

        using var client = CreateClient(accessKeyId, secretAccessKey, regionName);
        await ImportKeyPairAsync(client, keyPairName, publicKeyMaterial, cancellationToken);

        var network = await ResolveDefaultLaunchNetworkAsync(client, regionName, cancellationToken);
        var userData = Convert.ToBase64String(Encoding.UTF8.GetBytes(userDataScript));

        var runResponse = await client.RunInstancesAsync(new RunInstancesRequest
        {
            ImageId = amiId,
            InstanceType = type,
            MinCount = 1,
            MaxCount = 1,
            KeyName = keyPairName,
            SubnetId = network.SubnetId,
            SecurityGroupIds = [network.SecurityGroupId],
            UserData = userData,
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
        }, cancellationToken);

        var instance = runResponse.Reservation?.Instances.FirstOrDefault()
                       ?? throw new InvalidOperationException("AWS did not return a created instance.");

        var instanceId = instance.InstanceId
                         ?? throw new InvalidOperationException("AWS did not return an instance id.");

        return await WaitForRunningInstanceAsync(
            accessKeyId,
            secretAccessKey,
            regionName,
            instanceId,
            amiId,
            cancellationToken);
    }

    public async Task<AwsEc2Instance> WaitForRunningInstanceAsync(
        string accessKeyId,
        string secretAccessKey,
        string region,
        string instanceId,
        string? imageId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(accessKeyId, secretAccessKey, region);
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
        try
        {
            await client.ImportKeyPairAsync(new ImportKeyPairRequest
            {
                KeyName = keyPairName,
                PublicKeyMaterial = publicKeyMaterial.Trim(),
            }, cancellationToken);
        }
        catch (AmazonEC2Exception ex) when (ex.ErrorCode is "InvalidKeyPair.Duplicate")
        {
            await client.DeleteKeyPairAsync(new DeleteKeyPairRequest { KeyName = keyPairName }, cancellationToken);
            await client.ImportKeyPairAsync(new ImportKeyPairRequest
            {
                KeyName = keyPairName,
                PublicKeyMaterial = publicKeyMaterial.Trim(),
            }, cancellationToken);
        }
    }

    private sealed class AwsLaunchNetwork
    {
        public required string SubnetId { get; init; }

        public required string SecurityGroupId { get; init; }
    }

    private static async Task<AwsLaunchNetwork> ResolveDefaultLaunchNetworkAsync(
        IAmazonEC2 client,
        string region,
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

        var subnet = subnetResponse.Subnets.FirstOrDefault()
                     ?? throw new InvalidOperationException(
                         $"No default subnet found in the default VPC for {region}.");

        var securityGroupId = await ResolveLaunchSecurityGroupAsync(client, vpc.VpcId, cancellationToken);
        return new AwsLaunchNetwork
        {
            SubnetId = subnet.SubnetId,
            SecurityGroupId = securityGroupId,
        };
    }

    private static async Task<string> ResolveLaunchSecurityGroupAsync(
        IAmazonEC2 client,
        string vpcId,
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

        var group = existing.SecurityGroups.FirstOrDefault();
        if (group is not null)
        {
            return group.GroupId;
        }

        var created = await client.CreateSecurityGroupAsync(new CreateSecurityGroupRequest
        {
            GroupName = groupName,
            Description = "Azeroth Platform launch — SSH ingress for bootstrap",
            VpcId = vpcId,
        }, cancellationToken);

        await client.AuthorizeSecurityGroupIngressAsync(new AuthorizeSecurityGroupIngressRequest
        {
            GroupId = created.GroupId,
            IpPermissions =
            [
                new IpPermission
                {
                    IpProtocol = "tcp",
                    FromPort = 22,
                    ToPort = 22,
                    Ipv4Ranges =
                    [
                        new IpRange { CidrIp = "0.0.0.0/0", Description = "SSH for platform bootstrap" },
                    ],
                },
            ],
        }, cancellationToken);

        return created.GroupId;
    }

    private static async Task<AwsCatalogOption?> FindLatestImageAsync(
        IAmazonEC2 client,
        IReadOnlyList<string> owners,
        string namePattern,
        string labelPrefix,
        CancellationToken cancellationToken)
    {
        var response = await client.DescribeImagesAsync(new DescribeImagesRequest
        {
            Owners = owners.ToList(),
            Filters =
            [
                new Filter("name", [namePattern]),
                new Filter("state", ["available"]),
                new Filter("architecture", ["x86_64"]),
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

        var label = string.IsNullOrWhiteSpace(image.Name)
            ? labelPrefix
            : $"{labelPrefix} ({image.Name})";

        return new AwsCatalogOption
        {
            Value = image.ImageId,
            Label = label,
            Description = image.Description,
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

    private static bool IsPreferredLaunchInstanceType(string instanceType)
    {
        if (PreferredInstanceTypes.Contains(instanceType))
        {
            return true;
        }

        return PreferredInstanceTypePrefixes.Any(prefix =>
            instanceType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<AwsEc2Instance>> ListRunningInstancesInRegionAsync(
        string accessKeyId,
        string secretAccessKey,
        string region,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(accessKeyId, secretAccessKey, region);
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

    private static AmazonEC2Client CreateClient(string accessKeyId, string secretAccessKey, string region)
    {
        var credentials = new BasicAWSCredentials(accessKeyId.Trim(), secretAccessKey.Trim());
        var config = new AmazonEC2Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
        };
        return new AmazonEC2Client(credentials, config);
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
