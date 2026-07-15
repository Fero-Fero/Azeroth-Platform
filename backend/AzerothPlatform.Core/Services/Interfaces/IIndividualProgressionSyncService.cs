using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface IIndividualProgressionSyncService
{
    const string ModuleId = "mod-individual-progression";

    bool StackHasModule(IReadOnlyList<string> moduleIds);

    Task<IndividualProgressionSettingsDto> GetSettingsAsync(string stackId, CancellationToken cancellationToken = default);

    Task<IndividualProgressionSettingsDto> SaveSettingsAsync(
        string stackId,
        IndividualProgressionSettingsDto settings,
        CancellationToken cancellationToken = default);

    Task<IndividualProgressionSettingsDto> DiscoverAndMergeSettingsAsync(
        string stackId,
        IndividualProgressionSettingsDto? existing = null,
        CancellationToken cancellationToken = default);

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
}
