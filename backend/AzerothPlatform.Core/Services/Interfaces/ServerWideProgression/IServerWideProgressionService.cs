using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Server Wide Progression custom setup: bootstrap, patch templates, and sync from
/// <c>mod-individual-progression</c> plus Azeroth-Platform-Progression.
/// </summary>
public interface IServerWideProgressionService
{
    const string ModuleId = "mod-individual-progression";

    bool StackHasModule(IReadOnlyList<string> moduleIds);

    Task<ServerWideProgressionSettingsDto> GetSettingsAsync(string stackId, CancellationToken cancellationToken = default);

    Task<ServerWideProgressionBootstrapResultDto> BootstrapAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Creates any missing Server Wide Progression patch template folders (safe after patches are applied).</summary>
    Task<ServerWideProgressionRecreatePatchesResultDto> RecreateMissingPatchesAsync(
        string stackId,
        CancellationToken cancellationToken = default);

    Task OnPatchAppliedAsync(
        string stackId,
        string patchKey,
        PatchProgressionMetadataDto metadata,
        IList<string> applyLog,
        CancellationToken cancellationToken = default);

    Task<PatchProgressionMetadataDto?> ReadPatchMetadataAsync(string stackRoot, string patchKey);

    Task<ServerWideProgressionValidationResultDto> ValidatePatchesAsync(
        string stackId,
        CancellationToken cancellationToken = default);

    Task<(bool Allowed, string? Error)> CheckPatchApplyAllowedAsync(
        string stackId,
        CancellationToken cancellationToken = default);

    int CountProgressionPatches(string stackRoot);

    int GetExpectedProgressionPatchCount(string stackId);

    /// <summary>Returns the current sync status including whether an optional files log exists.</summary>
    Task<ProgressionSyncStatusDto> GetSyncStatusAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a full progression sync: pulls mod-individual-progression and Azeroth-Platform-Progression,
    /// creates stack patch folders from the progression repository, copies repository content, imports mapped
    /// module files, and returns any pending optional files.
    /// </summary>
    Task<ProgressionSyncResultDto> RunSyncAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Resolves pending optional files with the user's accept/ignore decisions.</summary>
    Task<ProgressionSyncResultDto> ResolveOptionalFilesAsync(
        string stackId,
        ResolveOptionalFilesRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the list of optional files the user previously ignored.</summary>
    Task<IReadOnlyList<ProgressionIgnoredFileDto>> GetIgnoredFilesAsync(
        string stackId,
        CancellationToken cancellationToken = default);

    /// <summary>Re-prompts a previously ignored file, marking it for inclusion on the next sync.</summary>
    Task<ProgressionSyncResultDto> RepromptIgnoredFileAsync(
        string stackId,
        string source,
        CancellationToken cancellationToken = default);
}
