using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Per-module extra-data recipe keyed by catalog id. Missing hook = skip (C++ / conf / data/sql only).
/// </summary>
public interface IModuleInstallHook
{
    string ModuleId { get; }

    Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default);

    Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default);
}

public sealed class ModuleInstallContext
{
    public required string ModuleId { get; init; }
    public required string PackageRoot { get; init; }
    public Guid? StackId { get; init; }
    public required IModuleInstallSession Session { get; init; }
    public required IModuleInstallHelpers Helpers { get; init; }
    public required ModuleInstallSelections Selections { get; init; }
}

/// <summary>Session surface hooks and helpers share. Implemented by the infrastructure session type.</summary>
public interface IModuleInstallSession
{
    string RootPath { get; }
    string ModuleDir(string moduleId);
    SessionBaseDbc? BaseDbc { get; }
    void SetBaseDbc(SessionBaseDbc value);
}

public interface IModuleInstallHelpers
{
    Task ExtractArchive(string relativeArchivePath, CancellationToken cancellationToken = default);
    Task ExtractAllDbcs(CancellationToken cancellationToken = default);
    Task ExtractDbcByName(string name, CancellationToken cancellationToken = default);
    Task ExtractDbcsFromMpq(string mpqPath, string? name = null, CancellationToken cancellationToken = default);
    void SetAsBaseDBC(string name);
    Task TrimAllDbcs(CancellationToken cancellationToken = default);
    Task IncludeSql(string relativePath, CancellationToken cancellationToken = default);
    Task IncludeMpq(string relativePath, CancellationToken cancellationToken = default);
    Task IncludeCsv(string relativePath, CancellationToken cancellationToken = default);
    Task IncludeCsvDirectory(string relativeDir, CancellationToken cancellationToken = default);
    Task PackMpqDirectory(string relativeDir, string mpqFileName, CancellationToken cancellationToken = default);
    Task IncludeMaps(string relativeDir, CancellationToken cancellationToken = default);
    Task IncludeAddon(string relativeDir, string folderName, CancellationToken cancellationToken = default);
    Task IncludeLua(string relativePath, string destRelativePath, CancellationToken cancellationToken = default);
    void AddConfHint(string key, string value);
    ModuleInstallContribution Contribution { get; }
}

public interface IModuleInstallHookRunner
{
    IModuleInstallHook? Find(string moduleId);
    IReadOnlyList<IModuleInstallHook> All { get; }
}
