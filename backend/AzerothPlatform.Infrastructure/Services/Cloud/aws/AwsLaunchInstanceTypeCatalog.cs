namespace AzerothPlatform.Infrastructure.Services.Cloud;

internal static class AwsLaunchInstanceTypeCatalog
{
    internal static readonly string[] FallbackFreeTierInstanceTypes =
    [
        "t3.micro",
        "t2.micro",
        "t3.small",
        "t4g.micro",
        "t4g.small",
        "c7i-flex.large",
        "m7i-flex.large",
    ];

    internal sealed record LaunchType(
        string Type,
        string Architecture,
        int VCpus,
        int MemoryMiB);

    internal static IReadOnlyList<LaunchType> SelectAvailable(
        IReadOnlyCollection<string> offeredInLocation,
        IReadOnlyList<LaunchType> freeTierEligible)
    {
        var offered = new HashSet<string>(offeredInLocation, StringComparer.OrdinalIgnoreCase);
        var selected = freeTierEligible
            .Where(type => offered.Contains(type.Type))
            .GroupBy(type => type.Type, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (selected.Count > 0)
        {
            return Sort(selected);
        }

        return Sort(FallbackFreeTierInstanceTypes
            .Where(offered.Contains)
            .Select(type => new LaunchType(
                type,
                type.StartsWith("t4g.", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x86_64",
                2,
                EstimateFallbackMemoryMiB(type)))
            .ToList());
    }

    internal static string FormatLabel(LaunchType type)
    {
        var memory = type.MemoryMiB >= 1024
            ? $"{Math.Max(1, type.MemoryMiB / 1024)} GiB"
            : $"{type.MemoryMiB} MiB";
        return $"{type.Type} — Free Tier · {type.VCpus} vCPU · {memory}";
    }

    private static IReadOnlyList<LaunchType> Sort(IReadOnlyList<LaunchType> types)
        => types
            .OrderBy(type => ArchitectureRank(type.Architecture))
            .ThenByDescending(type => type.MemoryMiB)
            .ThenByDescending(type => type.VCpus)
            .ThenBy(type => type.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int ArchitectureRank(string architecture)
        => architecture.Contains("x86", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static int EstimateFallbackMemoryMiB(string instanceType)
        => instanceType.ToLowerInvariant() switch
        {
            "m7i-flex.large" => 8192,
            "c7i-flex.large" => 4096,
            "t3.small" or "t4g.small" => 2048,
            _ => 1024,
        };
}
