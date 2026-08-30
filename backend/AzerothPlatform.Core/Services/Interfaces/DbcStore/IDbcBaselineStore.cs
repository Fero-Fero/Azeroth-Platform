using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// On-demand DBC CSV cache used as the trim/diff baseline. Tables are converted from a
/// <c>.dbc</c> (typically the stack data-volume copy) only when a patch or module needs that table.
/// </summary>
public interface IDbcBaselineStore
{
    DbcBaselineStoreDto GetStatus();

    /// <summary>Always true: baselines are produced per table, not via a bulk client-data sync.</summary>
    bool IsReady();

    /// <summary>Directory containing cached <c>{Table}.txt</c> CSVs.</summary>
    string? StoreDirectory { get; }

    string? FindTableCsv(string tableName);

    /// <summary>
    /// Returns a CSV for <paramref name="tableName"/>, exporting <paramref name="dbcPath"/> if the
    /// cache is missing or older than the DBC. Returns null when WDBX has no definition for the table.
    /// </summary>
    Task<string?> EnsureTableCsvAsync(
        string tableName, string dbcPath, CancellationToken cancellationToken = default);

    /// <summary>Clears the CSV cache when <paramref name="force"/> is true; otherwise a no-op.</summary>
    DbcBaselineStoreDto EnqueueSync(bool force = false);

    Task SyncAsync(bool force, Action<string>? onProgress, CancellationToken cancellationToken = default);
}
