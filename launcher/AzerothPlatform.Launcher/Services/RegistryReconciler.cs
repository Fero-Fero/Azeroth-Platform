using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>Outcome of reconciling the replicated registry across all known stacks.</summary>
public sealed class ReconcileResult
{
    /// <summary>The merged, deduped profiles document (branding from the newest snapshot + healthy stacks).</summary>
    public LauncherProfilesResponse Profiles { get; set; } = new();

    /// <summary>The self-healed set of known portal URLs to persist for next launch.</summary>
    public List<string> KnownServers { get; set; } = new();

    /// <summary>True when at least one known stack answered its <c>/portal</c>.</summary>
    public bool AnyReachable { get; set; }

    /// <summary>Portal URL of the healthy stack advertising the newest launcher build (self-update source).</summary>
    public string? BestLauncherPortalUrl { get; set; }

    /// <summary>Newest launcher version advertised across healthy stacks (for self-update comparison).</summary>
    public string? BestLauncherVersion { get; set; }
}

/// <summary>
/// Self-healing reconciliation of the replicated multi-stack registry: the launcher queries every known
/// stack's <c>/portal</c>, merges the registry entries keeping the newest <c>Revision</c> per stack,
/// health-pings each stack, dedupes, and derives the profile list + branding + self-update source. This
/// keeps multi-stack functionality without the manager: any single healthy stack re-teaches the launcher
/// the whole set, and stale/unreachable copies are overridden by newer ones.
/// </summary>
public sealed class RegistryReconciler
{
    public async Task<ReconcileResult> ReconcileCurrentAsync(string currentServer, CancellationToken cancellationToken)
    {
        var url = currentServer.TrimEnd('/');
        var doc = await new PortalClient(url, PortalClient.ProbeTimeout).GetPortalAsync(cancellationToken);
        if (doc is null)
        {
            return new ReconcileResult
            {
                KnownServers = new List<string> { url },
                AnyReachable = false
            };
        }

        var self = doc.Registry.FirstOrDefault(e =>
            string.Equals(e.StackId, doc.SelfStackId, StringComparison.Ordinal));
        if (self is null)
        {
            return new ReconcileResult
            {
                KnownServers = KnownServersFrom(url, doc),
                AnyReachable = true,
                Profiles = ProfilesFrom(doc, Array.Empty<StackRegistryEntry>()),
                BestLauncherPortalUrl = string.IsNullOrWhiteSpace(doc.Launcher.Version) ? null : url,
                BestLauncherVersion = string.IsNullOrWhiteSpace(doc.Launcher.Version) ? null : doc.Launcher.Version
            };
        }

        if (string.IsNullOrWhiteSpace(self.PortalUrl))
        {
            self.PortalUrl = url;
        }

        return new ReconcileResult
        {
            Profiles = ProfilesFrom(doc, new[] { self }, healthyStackIds: new[] { self.StackId }),
            KnownServers = KnownServersFrom(url, doc),
            AnyReachable = true,
            BestLauncherPortalUrl = string.IsNullOrWhiteSpace(doc.Launcher.Version) ? null : url,
            BestLauncherVersion = string.IsNullOrWhiteSpace(doc.Launcher.Version) ? null : doc.Launcher.Version
        };
    }

    public async Task<ReconcileResult> ReconcileAsync(IEnumerable<string> knownServers, CancellationToken cancellationToken)
    {
        var seeds = knownServers
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Newest entry per stack (by Revision), plus the branding snapshot from the newest push.
        var merged = new Dictionary<string, StackRegistryEntry>(StringComparer.Ordinal);
        StackPortalDocument? brandingDoc = null;
        var anyReachable = false;

        var portalDocs = await Task.WhenAll(seeds.Select(async url =>
        {
            var doc = await new PortalClient(url, PortalClient.ProbeTimeout).GetPortalAsync(cancellationToken);
            return (Url: url, Doc: doc);
        }));

        foreach (var (url, doc) in portalDocs)
        {
            if (doc is null)
            {
                continue;
            }

            anyReachable = true;
            if (brandingDoc is null || doc.RegistryRevision > brandingDoc.RegistryRevision)
            {
                brandingDoc = doc;
            }

            foreach (var entry in doc.Registry)
            {
                if (string.IsNullOrWhiteSpace(entry.StackId))
                {
                    continue;
                }

                // A stack that never received a manager push serves a fallback doc with a blank PortalUrl
                // for itself — we reached it at `url`, so fill it in.
                if (string.IsNullOrWhiteSpace(entry.PortalUrl)
                    && string.Equals(entry.StackId, doc.SelfStackId, StringComparison.Ordinal))
                {
                    entry.PortalUrl = url;
                }

                if (!merged.TryGetValue(entry.StackId, out var existing) || entry.Revision >= existing.Revision)
                {
                    merged[entry.StackId] = entry;
                }
            }
        }

        // Health-ping each stack we can address. Runs in parallel; failures just mark the stack unhealthy.
        var addressable = merged.Values.Where(e => !string.IsNullOrWhiteSpace(e.PortalUrl)).ToList();
        var health = new Dictionary<string, bool>(StringComparer.Ordinal);
        await Task.WhenAll(addressable.Select(async e =>
        {
            var ok = await new PortalClient(e.PortalUrl).PingHealthAsync(cancellationToken);
            lock (health) { health[e.StackId] = ok; }
        }));

        var profilesDoc = ProfilesFrom(
            brandingDoc,
            merged.Values,
            health.Where(kvp => kvp.Value).Select(kvp => kvp.Key));

        // Self-heal the known-server list: union of the seeds and every portal URL we now know about, so
        // the launcher can still reach the set next launch even if some seeds die.
        var knownOut = seeds
            .Concat(addressable.Select(e => e.PortalUrl.TrimEnd('/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Self-update source: the healthy stack advertising the newest launcher version.
        string? bestUrl = null;
        string? bestVersion = null;
        foreach (var entry in merged.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.PortalUrl) || string.IsNullOrWhiteSpace(entry.LauncherVersion))
            {
                continue;
            }

            if (!(health.TryGetValue(entry.StackId, out var ok) && ok))
            {
                continue;
            }

            if (bestVersion is null || CompareVersions(entry.LauncherVersion, bestVersion) > 0)
            {
                bestVersion = entry.LauncherVersion;
                bestUrl = entry.PortalUrl;
            }
        }

        return new ReconcileResult
        {
            Profiles = profilesDoc,
            KnownServers = knownOut,
            AnyReachable = anyReachable,
            BestLauncherPortalUrl = bestUrl,
            BestLauncherVersion = bestVersion,
        };
    }

    /// <summary>Combines a stack's portal base URL with a relative branding path, or null when either is blank.</summary>
    private static string? AbsoluteBrandingUrl(string portalUrl, string relative)
    {
        if (string.IsNullOrWhiteSpace(portalUrl) || string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        return $"{portalUrl.TrimEnd('/')}/{relative.TrimStart('/')}";
    }

    private static List<string> KnownServersFrom(string seed, StackPortalDocument doc) =>
        new[] { seed }
            .Concat(doc.Registry.Select(e => e.PortalUrl).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.TrimEnd('/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static LauncherProfilesResponse ProfilesFrom(
        StackPortalDocument? brandingDoc,
        IEnumerable<StackRegistryEntry> entries,
        IEnumerable<string>? healthyStackIds = null)
    {
        var healthy = new HashSet<string>(healthyStackIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var profilesDoc = new LauncherProfilesResponse
        {
            AppName = brandingDoc?.AppName ?? "Azeroth Platform",
            BrandingTitle = string.IsNullOrWhiteSpace(brandingDoc?.BrandingTitle) ? "Azeroth Platform Launcher" : brandingDoc!.BrandingTitle,
            Template = brandingDoc?.Template ?? string.Empty,
            AccentColor = brandingDoc?.AccentColor ?? string.Empty,
        };

        foreach (var entry in entries.OrderBy(e => e.SortOrder).ThenBy(e => e.DisplayName))
        {
            profilesDoc.Profiles.Add(new LauncherProfile
            {
                StackId = entry.StackId,
                DisplayName = entry.DisplayName,
                Description = entry.Description,
                SortOrder = entry.SortOrder,
                RealmlistHost = entry.RealmlistHost,
                RealmlistPort = entry.RealmlistPort,
                ArmoryPort = entry.ArmoryPort,
                PortalUrl = entry.PortalUrl,
                Healthy = healthy.Contains(entry.StackId),
                Template = entry.Template,
                AccentColor = entry.AccentColor,
                // Branding is hosted by each stack's own container; resolve the advertised relative paths
                // into absolute URLs against that stack's portal so the launcher fetches them directly.
                BackgroundUrl = AbsoluteBrandingUrl(entry.PortalUrl, entry.BackgroundUrl),
                LogoUrl = AbsoluteBrandingUrl(entry.PortalUrl, entry.LogoUrl),
                NewsUrl = AbsoluteBrandingUrl(entry.PortalUrl, entry.NewsUrl),
                ClientVersion = entry.ClientVersion,
            });
        }

        return profilesDoc;
    }

    /// <summary>Numeric compare of Release.Update.Minor.Patch versions (1.2.10 &gt; 1.2.9).</summary>
    private static int CompareVersions(string a, string b)
    {
        var sa = Parse(a);
        var sb = Parse(b);
        for (var i = 0; i < 4; i++)
        {
            var c = sa[i].CompareTo(sb[i]);
            if (c != 0) { return c; }
        }
        return 0;

        static int[] Parse(string v)
        {
            var segments = new int[4];
            var parts = v.Split('.');
            for (var i = 0; i < 4 && i < parts.Length; i++)
            {
                int.TryParse(parts[i], out segments[i]);
            }
            return segments;
        }
    }
}
