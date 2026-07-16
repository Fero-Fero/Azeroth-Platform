using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

// DTOs are imported from AzerothPlatform.Core.Contracts (ProgressionSyncStatusDto, etc.)

public interface IIndividualProgressionSyncService
{
    const string ModuleId = "mod-individual-progression";

    bool StackHasModule(IReadOnlyList<string> moduleIds);

    Task<IndividualProgressionSettingsDto> GetSettingsAsync(string stackId, CancellationToken cancellationToken = default);

    Task<IndividualProgressionBootstrapResultDto> BootstrapAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Creates any missing Individual Progression patch template folders (safe after patches are applied).</summary>
    Task<IndividualProgressionRecreatePatchesResultDto> RecreateMissingPatchesAsync(
        string stackId,
        CancellationToken cancellationToken = default);

    Task OnPatchAppliedAsync(
        string stackId,
        PatchProgressionMetadataDto metadata,
        IList<string> applyLog,
        CancellationToken cancellationToken = default);

    Task<PatchProgressionMetadataDto?> ReadPatchMetadataAsync(string stackRoot, string patchKey);

    Task<IndividualProgressionValidationResultDto> ValidatePatchesAsync(
        string stackId,
        CancellationToken cancellationToken = default);

    Task<(bool Allowed, string? Error)> CheckPatchApplyAllowedAsync(
        string stackId,
        CancellationToken cancellationToken = default);

    int CountProgressionPatches(string stackRoot);

    // ===== Progression Sync (mod-individual-progression + Azeroth-Platform-Progression) =====

    /// <summary>Returns the current sync status including whether an optional files log exists.</summary>
    Task<ProgressionSyncStatusDto> GetSyncStatusAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a full progression sync: fetches latest mod-individual-progression and
    /// Azeroth-Platform-Progression content, applies mappings, and returns any pending optional files.
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
