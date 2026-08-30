using AzerothPlatform.Infrastructure.Data.Entities;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Copies snapshot version fields onto the live stack so Overview can re-offer Update stack
/// after a checkpoint restore.
/// </summary>
internal static class RevisionVersionRestore
{
    public static void ApplySnapshotMetadata(ManagedStackEntity stack, StackRevisionEntity revision)
    {
        stack.CoreCommitSha = revision.CoreCommitSha;
        stack.ModuleVersionsJson = revision.ModuleVersionsJson;
        stack.AppliedPatchLevel = revision.AppliedPatchLevel;
        stack.AppliedPatchesJson = revision.AppliedPatchesJson;
    }

    public static void MarkOutdatedWhenCheckFails(ManagedStackEntity stack)
    {
        stack.IsOutdated = true;
        stack.IsCoreOutdated = !string.Equals(
            stack.CoreCommitSha, stack.LatestAvailableCoreSha, StringComparison.OrdinalIgnoreCase);
    }
}
