using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Manages each stack's base WoW client in its <c>client-base</c> Docker volume (volume-first; no persistent
/// manager mirror). Admins upload on a stack's Client tab; content is extracted into the stack volume
/// directly.
/// </summary>
public sealed class ClientService : IClientService
{
    private readonly ClientDistributionOptions _options;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClientService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientService(
        IOptions<ClientDistributionOptions> options,
        IRemoteEngineService remoteEngine,
        IServiceScopeFactory scopeFactory,
        ILogger<ClientService> logger)
    {
        _options = options.Value;
        _remoteEngine = remoteEngine;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private static string ClientBaseVolume(string stackId) =>
        DockerComposeOverrideGenerator.ClientBaseVolumeName(stackId);

    private async Task<ManagedStackEntity?> GetStackAsync(string stackId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        return await db.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
    }

    public async Task<ClientBaseInfoDto> GetBaseInfoAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, ClientBaseVolume(stackId), cancellationToken);
        return BuildInfo(stackId, summary);
    }

    public async Task<ClientBaseInfoDto> RescanBaseAsync(string stackId, CancellationToken cancellationToken = default)
        => await GetBaseInfoAsync(stackId, cancellationToken);

    public async Task<ClientBrowseResultDto> BrowseAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var normalized = NormalizeRelative(relativePath);
        var result = new ClientBrowseResultDto { Path = normalized };

        var volumeName = ClientBaseVolume(stackId);
        var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);
        if (!summary.VolumeExists)
        {
            return result;
        }

        var entries = await _remoteEngine.ListVolumeDirectoryAsync(stack, volumeName, normalized, cancellationToken);
        if (entries.Count == 0 && normalized.Length > 0)
        {
            return result;
        }

        result.Exists = normalized.Length == 0 ? summary.FileCount > 0 : entries.Count > 0;
        foreach (var entry in entries)
        {
            if (entry.Name is ".hashcache.json" or ".manifest.json")
            {
                continue;
            }

            result.Entries.Add(new ClientBrowseEntryDto
            {
                Name = entry.Name,
                IsDirectory = entry.IsDirectory,
                Size = entry.IsDirectory ? 0 : entry.SizeBytes,
                ItemCount = entry.IsDirectory ? entry.ItemCount : 0,
                RelativePath = entry.RelativePath,
            });
        }

        return result;
    }

    public async Task<ClientBaseInfoDto> DeleteEntryAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRelative(relativePath);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Refusing to delete the base client root. Delete individual files or folders instead.");
        }

        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");
        var volumeName = ClientBaseVolume(stackId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _remoteEngine.DeleteVolumePathsAsync(stack, volumeName, [normalized], cancellationToken);
            _logger.LogInformation("Deleted '{Path}' from base client volume for stack {StackId}.", normalized, stackId);
        }
        finally
        {
            _gate.Release();
        }

        return await GetBaseInfoAsync(stackId, cancellationToken);
    }

    public async Task<ClientBaseInfoDto> UploadFileAsync(
        string stackId, string relativeDir, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");
        var safeName = SanitizeFileName(fileName);
        var normalizedDir = NormalizeRelative(relativeDir);
        var relativeFile = CombineRelative(normalizedDir, safeName);
        ValidateVolumeRelative(relativeFile);

        var stagingDir = Path.Combine(Path.GetTempPath(), "azp-client-upload", stackId, Guid.NewGuid().ToString("N"));
        var targetFile = Path.Combine(stagingDir, relativeFile.Replace('/', Path.DirectorySeparatorChar));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await using (var file = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            await _remoteEngine.SeedVolumeAsync(stack, ClientBaseVolume(stackId), stagingDir, cancellationToken);
            _logger.LogInformation("Uploaded '{Path}' into base client volume for stack {StackId}.", relativeFile, stackId);
        }
        finally
        {
            TryDelete(stagingDir, isDirectory: true);
            _gate.Release();
        }

        return await GetBaseInfoAsync(stackId, cancellationToken);
    }

    public async Task<ClientBaseInfoDto> UploadBaseClientAsync(string stackId, Stream archiveStream, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");

        await _gate.WaitAsync(cancellationToken);
        var stagingDir = Path.Combine(Path.GetTempPath(), "azp-client-upload", stackId, Guid.NewGuid().ToString("N"));
        var tempArchive = Path.Combine(stagingDir, "upload.archive");
        var tempExtract = Path.Combine(stagingDir, "extract");
        try
        {
            Directory.CreateDirectory(stagingDir);

            await using (var file = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await archiveStream.CopyToAsync(file, cancellationToken);
            }

            Directory.CreateDirectory(tempExtract);
            _logger.LogInformation("Extracting uploaded base client archive for stack {StackId} to temp {Dir}...", stackId, tempExtract);
            ExtractArchive(tempArchive, tempExtract, cancellationToken);

            var clientRoot = FindClientRoot(tempExtract)
                ?? throw new InvalidOperationException(
                    "The uploaded archive does not look like a WoW client (no Wow.exe or Data/*.MPQ found).");

            var volumeName = ClientBaseVolume(stackId);
            await _remoteEngine.ClearVolumeContentsAsync(stack, volumeName, cancellationToken);
            await _remoteEngine.SeedVolumeAsync(stack, volumeName, clientRoot, cancellationToken);

            _logger.LogInformation("Base client for stack {StackId} installed in volume {Volume}.", stackId, volumeName);
            return BuildInfo(stackId, await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken));
        }
        finally
        {
            TryDelete(stagingDir, isDirectory: true);
            _gate.Release();
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            throw new InvalidOperationException("A valid file name is required.");
        }
        return name;
    }

    private static string NormalizeRelative(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/').Trim('/');

    private static void ValidateVolumeRelative(string normalizedRelative)
    {
        if (normalizedRelative.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Invalid path.");
        }
    }

    private static string CombineRelative(string parent, string name)
        => string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";

    private static void ExtractArchive(string archivePath, string destination, CancellationToken cancellationToken)
    {
        try
        {
            if (IsSevenZip(archivePath))
            {
                using var archive = ArchiveFactory.OpenArchive(new FileInfo(archivePath));
                using var reader = archive.ExtractAllEntries();
                ExtractEntries(reader, destination, cancellationToken);
            }
            else
            {
                using var stream = File.OpenRead(archivePath);
                using var reader = ReaderFactory.OpenReader(stream);
                ExtractEntries(reader, destination, cancellationToken);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The uploaded file could not be extracted ({ex.Message}). Supported formats are zip, rar, 7z, and tar (optionally gzip/bzip2/xz compressed).",
                ex);
        }
    }

    private static void ExtractEntries(IReader reader, string destination, CancellationToken cancellationToken)
    {
        var options = new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true,
            PreserveFileTime = false,
        };
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            EnsureEntryWithinDestination(destination, reader.Entry.Key);
            reader.WriteEntryToDirectory(destination, options);
        }
    }

    private static bool IsSevenZip(string archivePath)
    {
        try
        {
            Span<byte> sig = stackalloc byte[6];
            using var fs = File.OpenRead(archivePath);
            return fs.Read(sig) == 6
                && sig[0] == 0x37 && sig[1] == 0x7A && sig[2] == 0xBC
                && sig[3] == 0xAF && sig[4] == 0x27 && sig[5] == 0x1C;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureEntryWithinDestination(string destination, string? entryKey)
    {
        var key = (entryKey ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (key.Length == 0)
        {
            throw new InvalidOperationException("The archive contains an entry with an empty path.");
        }

        var destFull = Path.GetFullPath(destination);
        var destWithSep = destFull.EndsWith(Path.DirectorySeparatorChar)
            ? destFull
            : destFull + Path.DirectorySeparatorChar;

        var target = Path.GetFullPath(Path.Combine(destFull, key));
        if (target != destFull && !target.StartsWith(destWithSep, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Archive entry escapes the extraction directory: {entryKey}");
        }
    }

    private static string? FindClientRoot(string extractedRoot)
    {
        if (LooksLikeClientRoot(extractedRoot))
        {
            return extractedRoot;
        }

        foreach (var dir in Directory.EnumerateDirectories(extractedRoot))
        {
            if (LooksLikeClientRoot(dir))
            {
                return dir;
            }

            foreach (var nested in Directory.EnumerateDirectories(dir))
            {
                if (LooksLikeClientRoot(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool LooksLikeClientRoot(string dir)
        => HasWowExeAt(dir) || HasDataMpqAt(dir);

    private static bool HasWowExeAt(string dir)
        => Directory.EnumerateFiles(dir, "Wow.exe").Any()
        || Directory.EnumerateFiles(dir, "WoW.exe").Any();

    private static bool HasDataMpqAt(string dir)
    {
        var dataDir = Path.Combine(dir, "Data");
        return Directory.Exists(dataDir) && Directory.EnumerateFiles(dataDir, "*.MPQ").Any();
    }

    private static ClientBaseInfoDto BuildInfo(string stackId, VolumeTreeSummary summary)
    {
        var volumeName = ClientBaseVolume(stackId);
        return new ClientBaseInfoDto
        {
            GamePath = $"docker://{volumeName}",
            Exists = summary.FileCount > 0,
            FileCount = summary.FileCount,
            TotalSize = summary.TotalBytes,
            HasWowExe = summary.HasWowExe,
            HasDataMpq = summary.HasDataMpq,
        };
    }

    private void TryDelete(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean up temp path {Path}", path);
        }
    }
}
