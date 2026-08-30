using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Canonical stack image names and the extra tag that keeps a pre-update image from being GC'd
/// when compose rebuilds <c>:{stackId}</c>.
/// </summary>
internal static class RevisionCheckpointImages
{
    public const string RevisionTagInfix = "-rev-";

    public static IReadOnlyList<string> CanonicalTags(string stackId) =>
    [
        $"localhost/acore/ac-wotlk-worldserver:{stackId}",
        $"localhost/acore/ac-wotlk-authserver:{stackId}",
        $"localhost/acore/ac-wotlk-db-import:{stackId}",
        $"localhost/acore/ac-wotlk-client-data:{stackId}",
        $"acore/ac-wotlk-worldserver:{stackId}",
        $"acore/ac-wotlk-authserver:{stackId}",
        $"acore/ac-wotlk-db-import:{stackId}",
        $"acore/ac-wotlk-client-data:{stackId}",
        LlmChatterBridge.ImageTag(stackId)
    ];

    public static string CheckpointTag(string canonicalTag, string revisionId) =>
        $"{canonicalTag}{RevisionTagInfix}{revisionId}";

    public static bool BlocksRestoreWhileLive(StackStatus status) =>
        status is StackStatus.Running
            or StackStatus.Starting
            or StackStatus.Initializing
            or StackStatus.Degraded;
}
