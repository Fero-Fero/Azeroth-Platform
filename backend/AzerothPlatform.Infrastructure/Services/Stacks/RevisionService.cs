using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Patches;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Creates and restores point-in-time snapshots of a stack's databases + server config. Dumps are
/// captured with <c>mysqldump</c> in the stack's database container and written straight to disk
/// under <c>{stackRoot}/revisions/{id}/</c>; restore drops/recreates the databases and pipes each
/// dump back through the <c>mysql</c> client, then restores the snapshotted .conf files.
/// </summary>
public sealed class RevisionService : IRevisionService
{
    // File name (in the revision dir) -> AzerothCore schema name.
    private static readonly IReadOnlyDictionary<string, string> Databases = new Dictionary<string, string>
    {
        ["world"] = "acore_world",
        ["auth"] = "acore_auth",
        ["characters"] = "acore_characters"
    };

    private static readonly Regex PasswordArgRegex = new(@"(?<=^|\s)-p[^\s-]\S*", RegexOptions.Compiled);

    private readonly AzerothCoreDbContext _dbContext;
    private readonly DockerOptions _dockerOptions;
    private readonly ILogger<RevisionService> _logger;

    public RevisionService(
        AzerothCoreDbContext dbContext,
        IOptions<DockerOptions> dockerOptions,
        ILogger<RevisionService> logger)
    {
        _dbContext = dbContext;
        _dockerOptions = dockerOptions.Value;
        _logger = logger;
    }

    private string BaseDir => Path.IsPathRooted(_dockerOptions.BuildsPath)
        ? _dockerOptions.BuildsPath
        : Path.GetFullPath(_dockerOptions.BuildsPath);

    private string GetStackRoot(string stackId) => Path.Combine(BaseDir, stackId);

    private string RepoPath(string stackId) => Path.Combine(GetStackRoot(stackId), "azerothcore-wotlk");

    public async Task<IReadOnlyList<RevisionDto>> ListAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var revisions = await _dbContext.StackRevisions
            .Where(revision => revision.StackId == stackId)
            .OrderByDescending(revision => revision.CreatedAt)
            .ToListAsync(cancellationToken);

        return revisions.Select(ToDto).ToList();
    }

    public async Task<RevisionDto> CreateAsync(string stackId, string reason, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);

        var revision = new StackRevisionEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            StackId = stackId,
            CreatedAt = DateTime.UtcNow,
            Reason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason,
            Status = "creating",
            CoreCommitSha = stack.CoreCommitSha,
            ModuleVersionsJson = stack.ModuleVersionsJson,
            AppliedPatchLevel = stack.AppliedPatchLevel,
            AppliedPatchesJson = stack.AppliedPatchesJson
        };

        _dbContext.StackRevisions.Add(revision);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var revisionDir = MigrationLayout.RevisionDir(stackRoot, revision.Id);

        try
        {
            Directory.CreateDirectory(revisionDir);

            _logger.LogInformation(
                "Creating {Reason} revision {RevisionId} for stack {StackId}", revision.Reason, revision.Id, stackId);

            await EnsureDatabaseUpAsync(stackId, stack.StackName, stack.DatabaseRootPassword, cancellationToken);

            var container = DbContainer(stackId, stack.StackName);
            long totalSize = 0;
            foreach (var (fileName, schema) in Databases)
            {
                var outFile = Path.Combine(revisionDir, $"{fileName}.sql");
                totalSize += await DumpDatabaseAsync(container, stack.DatabaseRootPassword, schema, outFile, cancellationToken);
            }

            // Snapshot the server .conf files alongside the dumps.
            var etcDir = MigrationLayout.EtcDir(stackRoot);
            if (Directory.Exists(etcDir))
            {
                var confDest = Path.Combine(revisionDir, "conf");
                totalSize += CopyConfFiles(etcDir, confDest);
            }

            revision.SizeBytes = totalSize;
            revision.Status = "ready";

            await File.WriteAllTextAsync(
                Path.Combine(revisionDir, "metadata.json"),
                JsonSerializer.Serialize(ToDto(revision), new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Created revision {RevisionId} for stack {StackId} ({Size} bytes)", revision.Id, stackId, totalSize);

            return ToDto(revision);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create revision {RevisionId} for stack {StackId}", revision.Id, stackId);
            revision.Status = "failed";
            revision.Error = ex.Message;
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            TryDeleteDirectory(revisionDir);
            throw;
        }
    }

    public async Task RestoreAsync(string stackId, string revisionId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);

        var revision = await _dbContext.StackRevisions
            .SingleOrDefaultAsync(r => r.Id == revisionId && r.StackId == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Revision not found: {revisionId}");

        if (revision.Status != "ready")
        {
            throw new InvalidOperationException($"Revision {revisionId} is not restorable (status: {revision.Status}).");
        }

        var revisionDir = MigrationLayout.RevisionDir(stackRoot, revisionId);
        if (!Directory.Exists(revisionDir))
        {
            throw new InvalidOperationException($"Revision files missing on disk for {revisionId}.");
        }

        _logger.LogInformation("Restoring revision {RevisionId} into stack {StackId}", revisionId, stackId);

        await EnsureDatabaseUpAsync(stackId, stack.StackName, stack.DatabaseRootPassword, cancellationToken);
        var container = DbContainer(stackId, stack.StackName);

        foreach (var (fileName, schema) in Databases)
        {
            var dumpFile = Path.Combine(revisionDir, $"{fileName}.sql");
            if (!File.Exists(dumpFile))
            {
                _logger.LogWarning("Revision {RevisionId} has no dump for {Schema}; skipping", revisionId, schema);
                continue;
            }

            // Drop + recreate so the restore is a clean rollback (removes rows/tables not in the dump).
            var recreate = await RunProcessAsync(
                "docker",
                $"exec {container} mysql -uroot -p{stack.DatabaseRootPassword} -e \"DROP DATABASE IF EXISTS {schema}; CREATE DATABASE {schema};\"",
                stdin: null, cancellationToken);
            if (recreate.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to reset {schema}: {FilterMySqlWarnings(recreate.StdErr)}");
            }

            await using var stream = new FileStream(dumpFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var import = await RunProcessAsync(
                "docker",
                $"exec -i {container} mysql -uroot -p{stack.DatabaseRootPassword} {schema}",
                stdin: stream, cancellationToken);
            if (import.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to restore {schema}: {FilterMySqlWarnings(import.StdErr)}");
            }
        }

        // Restore the .conf files if they were captured.
        var confSource = Path.Combine(revisionDir, "conf");
        var etcDir = MigrationLayout.EtcDir(stackRoot);
        if (Directory.Exists(confSource) && Directory.Exists(etcDir))
        {
            foreach (var file in Directory.EnumerateFiles(confSource, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(confSource, file);
                var dest = Path.Combine(etcDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }

        _logger.LogInformation("Restored revision {RevisionId} into stack {StackId}", revisionId, stackId);
    }

    public async Task DeleteAsync(string stackId, string revisionId, CancellationToken cancellationToken = default)
    {
        var revision = await _dbContext.StackRevisions
            .SingleOrDefaultAsync(r => r.Id == revisionId && r.StackId == stackId, cancellationToken);
        if (revision is null)
        {
            return;
        }

        TryDeleteDirectory(MigrationLayout.RevisionDir(GetStackRoot(stackId), revisionId));
        _dbContext.StackRevisions.Remove(revision);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted revision {RevisionId} for stack {StackId}", revisionId, stackId);
    }

    // ===== Helpers =====

    private static RevisionDto ToDto(StackRevisionEntity revision) => new()
    {
        Id = revision.Id,
        StackId = revision.StackId,
        CreatedAt = revision.CreatedAt,
        Reason = revision.Reason,
        Status = revision.Status,
        Error = revision.Error,
        CoreCommitSha = revision.CoreCommitSha,
        AppliedPatchLevel = revision.AppliedPatchLevel,
        SizeBytes = revision.SizeBytes
    };

    private async Task<ManagedStackEntity> GetStackAsync(string stackId, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        return stack ?? throw new KeyNotFoundException($"Stack not found: {stackId}");
    }

    private static string DbContainer(string stackId, string? stackName) =>
        $"{DockerComposeOverrideGenerator.GetContainerPrefix(stackId, stackName)}-database";

    private static long CopyConfFiles(string etcDir, string destDir)
    {
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(etcDir, "*.conf", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(etcDir, file);
            var dest = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            size += new FileInfo(dest).Length;
        }
        return size;
    }

    private async Task EnsureDatabaseUpAsync(string stackId, string? stackName, string rootPassword, CancellationToken cancellationToken)
    {
        var repoPath = RepoPath(stackId);
        if (Directory.Exists(repoPath))
        {
            await RunComposeAsync(stackId, "up -d ac-database", repoPath, cancellationToken);
        }

        var container = DbContainer(stackId, stackName);
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ping = await RunProcessAsync(
                "docker",
                $"exec {container} mysqladmin ping -uroot -p{rootPassword} --silent",
                stdin: null, cancellationToken);
            if (ping.ExitCode == 0)
            {
                return;
            }
            await Task.Delay(2000, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for the database to become ready for snapshot/restore.");
    }

    private async Task<long> DumpDatabaseAsync(
        string container, string rootPassword, string schema, string outFile, CancellationToken cancellationToken)
    {
        var arguments =
            $"exec {container} mysqldump -uroot -p{rootPassword} --single-transaction --skip-lock-tables " +
            $"--routines --triggers --events {schema}";

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogDebug("exec: docker {Args}", RedactSecrets(arguments));

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start mysqldump process.");
        }

        process.BeginErrorReadLine();

        // Stream stdout straight to the dump file instead of buffering a potentially large DB in memory.
        await using (var fileStream = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await process.StandardOutput.BaseStream.CopyToAsync(fileStream, cancellationToken);
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mysqldump {schema} failed: {FilterMySqlWarnings(stderr.ToString())}");
        }

        return new FileInfo(outFile).Length;
    }

    private async Task RunComposeAsync(string stackId, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var (command, argPrefix) = DockerComposeHelper.GetDockerCompose(_dockerOptions.ComposeCommand);
        var project = DockerComposeOverrideGenerator.GetComposeProjectName(stackId);
        var fullArgs = string.IsNullOrEmpty(argPrefix)
            ? $"--project-name {project} {arguments}"
            : $"{argPrefix} --project-name {project} {arguments}";

        var run = await RunProcessAsync(command, fullArgs, stdin: null, cancellationToken, workingDirectory,
            new Dictionary<string, string> { ["COMPOSE_PROJECT_NAME"] = project });

        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{command} {fullArgs} failed: {(string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr)}");
        }
    }

    private async Task<ProcessResult> RunProcessAsync(
        string fileName, string arguments, Stream? stdin, CancellationToken cancellationToken,
        string? workingDirectory = null, IDictionary<string, string>? env = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        _logger.LogDebug("exec: {Command} {Args}", fileName, RedactSecrets(arguments));

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await stdin.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string RedactSecrets(string arguments) => PasswordArgRegex.Replace(arguments, "-p***");

    private static string FilterMySqlWarnings(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return stderr;
        }

        var lines = stderr.Split('\n')
            .Where(line => !line.Contains("Using a password on the command line", StringComparison.OrdinalIgnoreCase));
        return string.Join("\n", lines).Trim();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}
