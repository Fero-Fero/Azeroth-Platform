namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Service for Git operations
/// </summary>
public interface IGitService
{
    /// <summary>
    /// Checks whether the git executable is available in the current environment.
    /// </summary>
    Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the branch names of a remote git repository without cloning it (via
    /// <c>git ls-remote --heads</c>). The repository URL must be a validated http(s) URL. Returns the
    /// branch names (short form, e.g. <c>master</c>), ordered alphabetically.
    /// </summary>
    Task<IReadOnlyList<string>> ListRemoteBranchesAsync(string repositoryUrl, CancellationToken cancellationToken = default);
}
