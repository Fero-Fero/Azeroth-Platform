using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface IModuleInstallOrchestrator
{
    Task<StackModuleInstallChoicesDto> DescribeChoicesAsync(
        string stackId, CancellationToken cancellationToken = default);

    Task SaveChoicesAsync(
        string stackId, ApplyModuleExtraDataRequest request, CancellationToken cancellationToken = default);

    ModuleExtraDataStackStatusDto GetStackStatus(string stackId);

    Task PrepareAsync(
        string stackId,
        ApplyModuleExtraDataRequest request,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default);

    Task DepositAsync(
        string stackId,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default);

    Task ApplyAsync(
        string stackId,
        ApplyModuleExtraDataRequest request,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default);

    Task RemoveModuleExtrasAsync(
        string stackId,
        string moduleId,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default);
}

public interface IModuleInstallJobService
{
    ModuleInstallJobStatusDto EnqueuePrepare(string stackId, ApplyModuleExtraDataRequest request);
    ModuleInstallJobStatusDto EnqueueDeposit(string stackId);
    ModuleInstallJobStatusDto Enqueue(string stackId, ApplyModuleExtraDataRequest request);
    ModuleInstallJobStatusDto? GetStatus(string stackId);
}

/// <summary>WDBXEditor CLI wrapper used by the DBC store and module install helpers.</summary>
public interface IWdbxCli
{
    Task ExportDbcToCsvAsync(string dbcPath, string csvPath, CancellationToken cancellationToken = default);
    Task ExtractDbcsFromMpqAsync(string mpqPath, string outputDir, string? filterName, CancellationToken cancellationToken = default);
    Task ImportCsvAsync(string dbcPath, string csvPath, CancellationToken cancellationToken = default);
}

/// <summary>mpqtool sidecar used to extract/repack overlay MPQs when stripping DBC files.</summary>
public interface IMpqToolCli
{
    Task ExtractAllAsync(string mpqPath, string outputDir, CancellationToken cancellationToken);
    Task PackPreservePathsAsync(string sourceDir, string outputMpq, CancellationToken cancellationToken);
}
