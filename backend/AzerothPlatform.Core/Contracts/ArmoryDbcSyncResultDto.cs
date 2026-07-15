namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Outcome of extracting the stack's live server DBCs and converting the armory-required tables to the
/// CSV files the armory reads (<c>data/dbc</c>).
/// </summary>
public sealed class ArmoryDbcSyncResultDto
{
    /// <summary>CSV files successfully produced and placed in the stack's armory dataset.</summary>
    public List<string> Exported { get; set; } = [];

    /// <summary>Tables that could not be converted (missing on the server or unsupported), with a reason.</summary>
    public List<string> Failed { get; set; } = [];

    /// <summary>Number of binary DBC files pulled from the stack's live data volume.</summary>
    public int ServerDbcCount { get; set; }
}
