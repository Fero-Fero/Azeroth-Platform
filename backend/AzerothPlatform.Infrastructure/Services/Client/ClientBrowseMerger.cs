using AzerothPlatform.ClientContent;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Merges one directory listing from the read-only base client volume with the per-stack overlay
/// (published patch MPQs and addons). Overlay entries win on name collision. Only the stock
/// Blizzard archives are locked; every other MPQ can be deleted.
/// </summary>
internal static class ClientBrowseMerger
{
    internal const string StockLockReason =
        "This is a default client archive and cannot be deleted.";

    public static List<ClientBrowseEntryDto> Merge(
        IReadOnlyList<VolumeDirectoryEntry> baseEntries,
        IReadOnlyList<VolumeDirectoryEntry> overlayEntries)
    {
        var merged = new Dictionary<string, ClientBrowseEntryDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in baseEntries)
        {
            var dto = ToDto(entry);
            if (dto is null)
            {
                continue;
            }

            merged[dto.Name] = dto;
        }

        foreach (var entry in overlayEntries)
        {
            var dto = ToDto(entry);
            if (dto is null)
            {
                continue;
            }

            if (merged.TryGetValue(dto.Name, out var existing) && existing.IsDirectory && dto.IsDirectory)
            {
                existing.ItemCount = Math.Max(existing.ItemCount, dto.ItemCount);
                continue;
            }

            merged[dto.Name] = dto;
        }

        return merged.Values
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ClientBrowseEntryDto? ToDto(VolumeDirectoryEntry entry)
    {
        if (entry.Name is ".hashcache.json" or ".manifest.json")
        {
            return null;
        }

        var locked = ClientBaseMergePolicy.IsProtectedStockMpq(entry.RelativePath);
        return new ClientBrowseEntryDto
        {
            Name = entry.Name,
            IsDirectory = entry.IsDirectory,
            Size = entry.IsDirectory ? 0 : entry.SizeBytes,
            ItemCount = entry.IsDirectory ? entry.ItemCount : 0,
            RelativePath = entry.RelativePath,
            IsLocked = locked,
            LockReason = locked ? StockLockReason : null,
        };
    }
}
