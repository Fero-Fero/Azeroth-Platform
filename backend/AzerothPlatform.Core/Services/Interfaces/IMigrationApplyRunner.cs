using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Runs patch apply / reapply operations in the background with a DB-backed cross-user lock, so an
/// apply cannot be started twice (by two operators on different machines) and its progress can be
/// polled and its trace log downloaded after the triggering HTTP request has returned.
/// </summary>
public interface IMigrationApplyRunner
{
    /// <summary>
    /// Claims the stack's apply lock and starts applying <paramref name="patchKey"/> in the background.
    /// Throws <see cref="InvalidOperationException"/> if an apply is already running for the stack.
    /// </summary>
    Task<ApplyStatusDto> StartApplyAsync(string stackId, string patchKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the stack's apply lock and starts reapplying all patch SQL in the background.
    /// Throws <see cref="InvalidOperationException"/> if an apply is already running for the stack.
    /// </summary>
    Task<ApplyStatusDto> StartReapplyAllAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Returns the current/last run status for a stack (or an idle status if none).</summary>
    ApplyStatusDto GetStatus(string stackId);

    /// <summary>
    /// Resolves the on-disk trace-log file for a run (the latest run when <paramref name="runId"/> is
    /// null), returning its absolute path and a suggested download file name, or null if none exists.
    /// </summary>
    (string Path, string FileName)? GetLogFile(string stackId, string? runId);
}
