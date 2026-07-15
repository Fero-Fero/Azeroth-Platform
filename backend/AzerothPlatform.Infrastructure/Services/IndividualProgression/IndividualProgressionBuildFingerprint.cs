using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Data.Entities;

namespace AzerothPlatform.Infrastructure.Services.IndividualProgression;

/// <summary>
/// Fingerprints a stack build so Individual Progression patch validation can be tied to the latest
/// server recompile. Validation must be re-run whenever the fingerprint changes.
/// </summary>
internal static class IndividualProgressionBuildFingerprint
{
    public static string? Compute(ManagedStackEntity stack)
    {
        if (string.IsNullOrWhiteSpace(stack.CoreCommitSha) || !stack.LastBuiltAt.HasValue)
        {
            return null;
        }

        return $"{stack.CoreCommitSha}|{stack.ModuleVersionsJson}|{stack.LastBuiltAt.Value.ToUniversalTime():O}";
    }

    public static bool IsCurrent(IndividualProgressionSettingsDto settings, ManagedStackEntity stack)
    {
        var current = Compute(stack);
        return current is not null
               && !string.IsNullOrWhiteSpace(settings.ValidationBuildFingerprint)
               && string.Equals(settings.ValidationBuildFingerprint, current, StringComparison.Ordinal);
    }
}
