using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Manages the per-stack BASE WoW client living under <c>{Client:RootPath}/stacks/{stackId}/game</c>.
/// Admins upload a base client archive on a stack's Client tab; this extracts it, validates it, and
/// re-seeds that stack's base client volume. Each stack's client container mounts its own seeded base
/// volume as the read-only base layer.
/// </summary>
public sealed class ClientService : IClientService
{
    private readonly ClientDistributionOptions _options;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly ILogger<ClientService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientService(
        IOptions<ClientDistributionOptions> options,
        IRemoteEngineService remoteEngine,
        ILogger<ClientService> logger)
    {
        _options = options.Value;
        _remoteEngine = remoteEngine;
        _logger = logger;
    }

    private string GameDir(string stackId) => _options.StackGameDir(stackId);

    public Task<ClientBaseInfoDto> GetBaseInfoAsync(string stackId, CancellationToken cancellationToken = default)
        => Task.FromResult(BuildInfo(GameDir(stackId)));

    public async Task<ClientBaseInfoDto> RescanBaseAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var gamePath = GameDir(stackId);
        // Re-seed the stack's base volume so a running/next stack serves the current base contents.
        try
        {
            if (Directory.Exists(gamePath))
            {
                await _remoteEngine.SeedLocalVolumeAsync(
                    DockerComposeOverrideGenerator.ClientBaseVolumeName(stackId), gamePath, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-seed the base client volume for stack {StackId}.", stackId);
        }

        return BuildInfo(gamePath);
    }

    public Task<ClientBrowseResultDto> BrowseAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var gamePath = GameDir(stackId);
        var normalized = NormalizeRelative(relativePath);
        var result = new ClientBrowseResultDto { Path = normalized };

        var target = ResolveWithinGame(gamePath, normalized);
        if (target is null || !Directory.Exists(target))
        {
            return Task.FromResult(result); // Exists stays false.
        }

        result.Exists = true;

        foreach (var dir in Directory.EnumerateDirectories(target))
        {
            var name = Path.GetFileName(dir);
            var childCount = 0;
            try
            {
                childCount = Directory.EnumerateFileSystemEntries(dir).Count();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to count children of {Dir}", dir);
            }

            result.Entries.Add(new ClientBrowseEntryDto
            {
                Name = name,
                IsDirectory = true,
                ItemCount = childCount,
                RelativePath = CombineRelative(normalized, name),
            });
        }

        foreach (var file in Directory.EnumerateFiles(target))
        {
            var name = Path.GetFileName(file);
            if (name is ".hashcache.json" or ".manifest.json")
            {
                continue;
            }

            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to stat {File}", file);
            }

            result.Entries.Add(new ClientBrowseEntryDto
            {
                Name = name,
                IsDirectory = false,
                Size = size,
                RelativePath = CombineRelative(normalized, name),
            });
        }

        // Sub-directories first, then files, each alphabetical (case-insensitive).
        result.Entries.Sort((a, b) =>
            a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(result);
    }

    public async Task<ClientBaseInfoDto> DeleteEntryAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var gamePath = GameDir(stackId);
        var normalized = NormalizeRelative(relativePath);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Refusing to delete the base client root. Delete individual files or folders instead.");
        }

        var target = ResolveWithinGame(gamePath, normalized)
            ?? throw new InvalidOperationException("Invalid path.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
            else
            {
                throw new InvalidOperationException("The file or folder no longer exists.");
            }

            _logger.LogInformation("Deleted '{Path}' from base client for stack {StackId}.", normalized, stackId);

            // Re-seed the base volume so a running/next stack reflects the removal. Best-effort.
            try
            {
                if (Directory.Exists(gamePath))
                {
                    await _remoteEngine.SeedLocalVolumeAsync(
                        DockerComposeOverrideGenerator.ClientBaseVolumeName(stackId), gamePath, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-seed the base client volume for stack {StackId} after delete.", stackId);
            }
        }
        finally
        {
            _gate.Release();
        }

        return BuildInfo(gamePath);
    }

    public async Task<ClientBaseInfoDto> UploadFileAsync(
        string stackId, string relativeDir, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var gamePath = GameDir(stackId);
        var safeName = SanitizeFileName(fileName);
        var normalizedDir = NormalizeRelative(relativeDir);

        var targetDir = ResolveWithinGame(gamePath, normalizedDir)
            ?? throw new InvalidOperationException("Invalid destination folder.");
        var targetFile = ResolveWithinGame(gamePath, CombineRelative(normalizedDir, safeName))
            ?? throw new InvalidOperationException("Invalid file path.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(targetDir);
            await using (var file = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            _logger.LogInformation("Uploaded '{Path}' into base client for stack {StackId}.",
                CombineRelative(normalizedDir, safeName), stackId);

            // Re-seed the base volume so a running/next stack serves the new file. Best-effort.
            try
            {
                if (Directory.Exists(gamePath))
                {
                    await _remoteEngine.SeedLocalVolumeAsync(
                        DockerComposeOverrideGenerator.ClientBaseVolumeName(stackId), gamePath, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-seed the base client volume for stack {StackId} after upload.", stackId);
            }
        }
        finally
        {
            _gate.Release();
        }

        return BuildInfo(gamePath);
    }

    /// <summary>Strips any directory components and rejects empty names so an upload can't escape its folder.</summary>
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

    /// <summary>
    /// Resolves <paramref name="normalizedRelative"/> against the base game directory, returning the
    /// absolute path only when it stays within the base (defends against <c>..</c> traversal).
    /// </summary>
    private static string? ResolveWithinGame(string gamePath, string normalizedRelative)
    {
        var basePath = Path.GetFullPath(gamePath);
        var combined = Path.GetFullPath(Path.Combine(basePath, normalizedRelative));
        if (combined != basePath &&
            !combined.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return combined;
    }

    private static string CombineRelative(string parent, string name)
        => string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";

    public async Task<ClientBaseInfoDto> UploadBaseClientAsync(string stackId, Stream archiveStream, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        var gamePath = GameDir(stackId);
        // Stage the archive + extraction next to the target game dir (same filesystem/mount) so the final
        // install is a same-device rename. Path.GetTempPath() is typically a different mount, which makes
        // Directory.Move fail with "Invalid cross-device link".
        var stackClientRoot = Path.GetDirectoryName(gamePath.TrimEnd(Path.DirectorySeparatorChar)) ?? _options.RootPath;
        Directory.CreateDirectory(stackClientRoot);
        var stagingDir = Path.Combine(stackClientRoot, $".upload-{Guid.NewGuid():N}");
        var tempArchive = Path.Combine(stagingDir, "upload.archive");
        var tempExtract = Path.Combine(stagingDir, "extract");
        try
        {
            Directory.CreateDirectory(stagingDir);

            // Stream the (potentially multi-GB) upload to a temp file first so extraction is seekable
            // (needed for random-access formats like zip/7z).
            await using (var file = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await archiveStream.CopyToAsync(file, cancellationToken);
            }

            Directory.CreateDirectory(tempExtract);
            _logger.LogInformation("Extracting uploaded base client archive for stack {StackId} to {Dir}...", stackId, tempExtract);
            ExtractArchive(tempArchive, tempExtract, cancellationToken);

            var clientRoot = FindClientRoot(tempExtract)
                ?? throw new InvalidOperationException(
                    "The uploaded archive does not look like a WoW client (no Wow.exe or Data/*.MPQ found).");

            // Atomically swap: move the new client into place, replacing any previous base. clientRoot is
            // under stagingDir (same filesystem as gamePath), so this is a same-device rename.
            if (Directory.Exists(gamePath))
            {
                Directory.Delete(gamePath, recursive: true);
            }
            Directory.Move(clientRoot, gamePath);

            _logger.LogInformation("Base client for stack {StackId} installed at {GamePath}.", stackId, gamePath);

            // Refresh this stack's base client volume on the local daemon so the running/next stack mounts
            // the new client (external hosts seed the volume at start). Best-effort: a seeding hiccup must
            // not fail the upload itself.
            try
            {
                await _remoteEngine.SeedLocalVolumeAsync(
                    DockerComposeOverrideGenerator.ClientBaseVolumeName(stackId), gamePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed the base client volume for stack {StackId} after upload.", stackId);
            }

            return BuildInfo(gamePath);
        }
        finally
        {
            TryDelete(stagingDir, isDirectory: true);
            _gate.Release();
        }
    }

    /// <summary>
    /// Extracts a base-client archive into <paramref name="destination"/>. The format (zip, rar, 7z,
    /// tar, tar.gz/bz2, …) is auto-detected from the file content, so the upload's extension is
    /// irrelevant. Uses a single sequential pass (<c>ExtractAllEntries</c>) which is efficient even for
    /// solid archives.
    /// </summary>
    private static void ExtractArchive(string archivePath, string destination, CancellationToken cancellationToken)
    {
        try
        {
            // 7z is random-access only; everything else (zip, rar, tar, and compressed tarballs
            // .tar.gz/.tar.bz2/.tar.xz) streams through ReaderFactory, which transparently unwraps the
            // outer compression and reads the inner tar. ArchiveFactory would treat a .tar.gz as a
            // single-entry gzip and never expose the contents, so this is required for those formats.
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
            // Surface the underlying reason so the operator knows what's actually wrong.
            throw new InvalidOperationException(
                $"The uploaded file could not be extracted ({ex.Message}). Supported formats are zip, rar, 7z, and tar (optionally gzip/bzip2/xz compressed).",
                ex);
        }
    }

    /// <summary>Writes every non-directory entry of <paramref name="reader"/> into the destination, guarding against zip-slip.</summary>
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
            // Zip-slip guard: reject any entry whose resolved path escapes the destination.
            EnsureEntryWithinDestination(destination, reader.Entry.Key);
            reader.WriteEntryToDirectory(destination, options);
        }
    }

    /// <summary>Detects a 7z archive by its 6-byte magic signature (37 7A BC AF 27 1C).</summary>
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

    /// <summary>
    /// Zip-slip guard: throws if an archive entry's resolved path would land outside
    /// <paramref name="destination"/> (e.g. a <c>../../</c> or absolute/rooted entry key).
    /// </summary>
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

    /// <summary>
    /// Locates the WoW install root inside an extracted tree: the directory that contains a
    /// <c>Wow.exe</c> or a <c>Data</c> folder with at least one <c>.MPQ</c>. Handles the common case of
    /// a single wrapping folder inside the zip. Searches up to two directory levels deep.
    /// </summary>
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

    private static ClientBaseInfoDto BuildInfo(string gamePath)
    {
        var info = new ClientBaseInfoDto { GamePath = gamePath };
        if (!Directory.Exists(gamePath))
        {
            return info;
        }

        long total = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(gamePath, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name is ".hashcache.json" or ".manifest.json")
            {
                continue;
            }
            count++;
            total += new FileInfo(file).Length;
        }

        info.Exists = count > 0;
        info.FileCount = count;
        info.TotalSize = total;
        info.HasWowExe = HasWowExeAt(gamePath);
        info.HasDataMpq = HasDataMpqAt(gamePath);
        return info;
    }

    private void TryDelete(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
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
