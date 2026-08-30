using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.Patches;

/// <summary>
/// Server Wide Progression DBC rules: binary <c>.dbc</c> only on expansion baselines (1.0 / 2.0 / 3.0).
/// Non-Express stacks may ship CSV/.txt on later tiers; those compile onto the captured server baseline.
/// Express stays baseline-only for all DBC.
/// </summary>
public static class SwpDbcPolicy
{
    public const string BinaryLaterMessage =
        "Binary DBC (.dbc) belongs on expansion baselines (1.0, 2.0, 3.0). Later patches may ship DBC as CSV or .txt only.";

    public static bool IsCsvSource(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBinary(string path) =>
        Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase);

    public static bool AllowCsvOnLaterTiers(ServerType serverType) =>
        serverType != ServerType.Express;

    /// <summary>
    /// Later-tier DBC folder: allow CSV/.txt when <paramref name="allowCsvOnLaterTiers"/> is true;
    /// never allow binary .dbc.
    /// </summary>
    public static bool IsAllowedLaterTierFile(string fileName, bool allowCsvOnLaterTiers) =>
        allowCsvOnLaterTiers && IsCsvSource(fileName);

    public static string SkipLog(string fileName) =>
        IsBinary(fileName)
            ? $"Skipped DBC '{fileName}' for non-baseline patch (binary DBC belongs on 1.0 / 2.0 / 3.0)."
            : $"Skipped DBC '{fileName}' for non-baseline patch (DBC belongs on 1.0 / 2.0 / 3.0).";
}
