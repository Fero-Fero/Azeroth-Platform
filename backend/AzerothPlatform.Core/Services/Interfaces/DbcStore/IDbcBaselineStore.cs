using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Manager-wide vanilla DBC CSV store (wowgaming/client-data). Used as the trim baseline for module extras.
/// </summary>
public interface IDbcBaselineStore
{
    DbcBaselineStoreDto GetStatus();

    bool IsReady();

    /// <summary>Directory containing <c>{Table}.txt</c> CSVs, or null when the store is not ready.</summary>
    string? StoreDirectory { get; }

    string? FindTableCsv(string tableName);

    /// <summary>Starts a background sync if none is running. If the store is already on the latest tag and <paramref name="force"/> is false, no-ops.</summary>
    DbcBaselineStoreDto EnqueueSync(bool force = false);

    Task SyncAsync(bool force, Action<string>? onProgress, CancellationToken cancellationToken = default);
}
