using System.IO.Compression;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Services.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// File-system backed store for uploaded module packages. Packages live under
/// <c>{dataDir}/custom-modules/{moduleId}</c> (a sibling of the builds directory, on the same
/// persistent data volume) so they survive restarts and can be copied into any new build.
/// </summary>
public sealed class ModulePackageStorage : IModulePackageStorage
{
    private static readonly string[] ReadmeCandidates =
    [
        "README.md", "Readme.md", "readme.md", "README.markdown", "README.MD", "README"
    ];

    private readonly string _root;
    private readonly ILogger<ModulePackageStorage> _logger;

    public ModulePackageStorage(IOptions<DockerOptions> dockerOptions, ILogger<ModulePackageStorage> logger)
    {
        _logger = logger;

        var buildsPath = dockerOptions.Value.BuildsPath;
        var buildsFull = Path.IsPathRooted(buildsPath) ? buildsPath : Path.GetFullPath(buildsPath);
        var dataDir = Path.GetDirectoryName(buildsFull.TrimEnd(Path.DirectorySeparatorChar)) ?? buildsFull;
        _root = Path.Combine(dataDir, "custom-modules");
    }

    public bool HasPackage(string moduleId) => Directory.Exists(GetPackageDir(moduleId));

    public async Task<int> SavePackageAsync(string moduleId, Stream zipContent, CancellationToken cancellationToken = default)
    {
        var dir = GetPackageDir(moduleId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);

        var extracted = await ExtractZipAsync(zipContent, dir, cancellationToken);
        _logger.LogInformation("Stored {Count} file(s) for module package {ModuleId} at {Dir}", extracted, moduleId, dir);
        return extracted;
    }

    public void DeletePackage(string moduleId)
    {
        var dir = GetPackageDir(moduleId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Deleted module package {ModuleId}", moduleId);
        }
    }

    public Task CopyToAsync(string moduleId, string destinationDir, CancellationToken cancellationToken = default)
    {
        var source = GetPackageDir(moduleId);
        if (!Directory.Exists(source))
        {
            throw new FileNotFoundException($"No stored package for module '{moduleId}'.");
        }

        CopyDirectory(source, destinationDir, cancellationToken);
        return Task.CompletedTask;
    }

    public async Task<string?> ReadReadmeAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var dir = GetPackageDir(moduleId);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        // Match a README file case-insensitively at the package root.
        var readme = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => ReadmeCandidates.Any(c =>
                string.Equals(Path.GetFileName(f), c, StringComparison.OrdinalIgnoreCase)));

        if (readme == null)
        {
            return null;
        }

        return await File.ReadAllTextAsync(readme, cancellationToken);
    }

    public string GetPackageDirectory(string moduleId) => GetPackageDir(moduleId);

    private string GetPackageDir(string moduleId)
    {
        // moduleId is validated (letters/digits/._-) before it reaches here, but guard anyway.
        if (moduleId.Contains('/') || moduleId.Contains('\\') || moduleId.Contains(".."))
        {
            throw new ArgumentException($"Invalid module id: {moduleId}");
        }
        return Path.Combine(_root, moduleId);
    }

    private static void CopyDirectory(string sourceDir, string destDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destDir);

        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Extracts a zip into <paramref name="destDir"/> (zip-slip guarded). If every entry lives under
    /// a single top-level folder (as produced by GitHub "Download ZIP" and most archivers), that
    /// wrapper folder is stripped so the module's files land at the package root.
    /// </summary>
    private static async Task<int> ExtractZipAsync(Stream zipContent, string destDir, CancellationToken cancellationToken)
    {
        using var scratch = TempWorkspace.CreateFile("module-package", ".zip");
        var tempFile = scratch.Path;
        try
        {
            await using (var fs = File.Create(tempFile))
            {
                await zipContent.CopyToAsync(fs, cancellationToken);
            }

            var destFull = Path.GetFullPath(destDir);
            var destWithSep = destFull.EndsWith(Path.DirectorySeparatorChar)
                ? destFull
                : destFull + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(tempFile);

            var rootPrefix = FindCommonRootPrefix(archive);

            var extractedFiles = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entryPath = entry.FullName.Replace('\\', '/');
                if (rootPrefix != null && entryPath.StartsWith(rootPrefix, StringComparison.Ordinal))
                {
                    entryPath = entryPath.Substring(rootPrefix.Length);
                }

                if (string.IsNullOrEmpty(entryPath))
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(destDir, entryPath));
                if (!target.StartsWith(destWithSep, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Zip entry escapes the package directory: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                extractedFiles++;
            }

            if (extractedFiles == 0)
            {
                throw new ArgumentException("The uploaded archive contained no files.");
            }

            return extractedFiles;
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException("The uploaded file is not a valid .zip archive.");
        }
    }

    /// <summary>
    /// Returns the shared top-level folder prefix (e.g. "mod-foo-master/") when all entries live
    /// under it, otherwise null.
    /// </summary>
    private static string? FindCommonRootPrefix(ZipArchive archive)
    {
        string? root = null;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var slash = path.IndexOf('/');
            if (slash < 0)
            {
                // A file at the archive root => no single wrapper folder.
                return null;
            }

            var top = path.Substring(0, slash + 1);
            if (root == null)
            {
                root = top;
            }
            else if (!string.Equals(root, top, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return root;
    }
}
