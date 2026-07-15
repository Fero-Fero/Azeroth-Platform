using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Populates a stack's armory model-viewer dataset with the DBC data the armory needs for rich
/// item/spell/mount tooltips. The armory reads a set of CSV files from <c>data/dbc</c>; this service
/// extracts the stack's live server DBCs (<c>/data/dbc</c> in the client-data volume), converts the
/// required tables to CSV with WDBXEditor, and writes them into the stack's uploaded armory dataset so
/// the next armory image build bakes them in. Extraction requires the stack to have started at least
/// once (so client-data-init populated the volume).
/// </summary>
public interface IArmoryDbcService
{
    /// <summary>
    /// Extracts the stack's server DBCs, converts the armory's required tables to CSV, and writes them
    /// into the stack's armory dataset (<c>data/dbc</c>). Does NOT rebuild the armory image or restart
    /// the armory — the caller (background job) is responsible for that. The optional
    /// <paramref name="onProgress"/> callback receives human-readable step lines for the job log.
    /// </summary>
    Task<ArmoryDbcSyncResultDto> SyncFromServerAsync(
        string stackId, Action<string>? onProgress = null, CancellationToken cancellationToken = default);

    /// <summary>True when the stack's armory dataset already contains extracted server DBC CSVs.</summary>
    bool HasServerDbcs(string stackId);
}
