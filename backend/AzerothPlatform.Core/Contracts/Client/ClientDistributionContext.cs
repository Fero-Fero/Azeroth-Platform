namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Describes a single distributable client root (either the global client or a per-stack client),
/// so the distribution service can scan/serve any root with its own realmlist and branding.
/// </summary>
public sealed class ClientDistributionContext
{
    /// <summary>
    /// Root directory holding <c>game/</c>, <c>settings/</c>, and an optional <c>launcher.json</c>.
    /// </summary>
    public required string RootPath { get; init; }

    public string GameExecutable { get; init; } = "Wow.exe";

    public string LaunchArguments { get; init; } = string.Empty;

    public string ClientVersion { get; init; } = "3.3.5a (12340)";

    public string BrandingTitle { get; init; } = "Azeroth Platform Launcher";

    public string RealmlistHost { get; init; } = "127.0.0.1";

    public int RealmlistPort { get; init; } = 3724;

    /// <summary>Relative prefixes (under game/) whose files are treated as "managed".</summary>
    public IReadOnlyList<string> ManagedPrefixes { get; init; } = new[] { "Data/patch-", "Interface/AddOns/" };
}
