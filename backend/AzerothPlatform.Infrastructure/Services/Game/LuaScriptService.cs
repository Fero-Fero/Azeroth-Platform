using System.IO.Compression;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services.Patches;
using AzerothPlatform.Infrastructure.Services.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Manages a stack's Lua scripts under <c>{BuildsPath}/{stackId}/lua_scripts</c>. The directory is
/// bind-mounted into the worldserver at Eluna's default ScriptPath, so changes take effect after a
/// worldserver restart (Eluna loads scripts at startup / on ".reload eluna").
/// </summary>
public sealed class LuaScriptService : ILuaScriptService
{
    private readonly string _buildsPath;
    private readonly AzerothCoreDbContext _dbContext;
    private readonly ILogger<LuaScriptService> _logger;

    public LuaScriptService(
        IOptions<DockerOptions> dockerOptions,
        AzerothCoreDbContext dbContext,
        ILogger<LuaScriptService> logger)
    {
        var buildsPath = dockerOptions.Value.BuildsPath;
        _buildsPath = Path.IsPathRooted(buildsPath) ? buildsPath : Path.GetFullPath(buildsPath);
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<LuaScriptListDto> ListAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var luaDir = GetLuaDir(stackId);
        Directory.CreateDirectory(luaDir);
        return await BuildListAsync(stackId, luaDir, cancellationToken);
    }

    public Task<LuaScriptContentDto> ReadAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var luaDir = GetLuaDir(stackId);
        var target = SafeResolve(luaDir, relativePath);
        if (!File.Exists(target))
        {
            throw new FileNotFoundException($"Lua script not found: {relativePath}");
        }

        return Task.FromResult(new LuaScriptContentDto
        {
            Path = NormalizeRelative(luaDir, target),
            Content = File.ReadAllText(target)
        });
    }

    public async Task<LuaScriptListDto> SaveAsync(string stackId, string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var luaDir = GetLuaDir(stackId);
        var target = SafeResolve(luaDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, content, cancellationToken);
        return await BuildListAsync(stackId, luaDir, cancellationToken);
    }

    public async Task<LuaScriptListDto> UploadAsync(
        string stackId,
        string fileName,
        string? relativePath,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var luaDir = GetLuaDir(stackId);
        Directory.CreateDirectory(luaDir);

        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Extract into the (optional) target sub-directory, preserving folder structure.
            var destDir = string.IsNullOrWhiteSpace(relativePath) ? luaDir : SafeResolve(luaDir, relativePath);
            Directory.CreateDirectory(destDir);
            var count = await ExtractZipAsync(content, destDir, cancellationToken);
            _logger.LogInformation("Extracted {Count} Lua file(s) into {Dir}", count, destDir);
        }
        else
        {
            var rel = string.IsNullOrWhiteSpace(relativePath) ? fileName : relativePath;
            var target = SafeResolve(luaDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var fs = File.Create(target);
            await content.CopyToAsync(fs, cancellationToken);
        }

        return await BuildListAsync(stackId, luaDir, cancellationToken);
    }

    public async Task<LuaScriptListDto> DeleteAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var luaDir = GetLuaDir(stackId);
        var target = SafeResolve(luaDir, relativePath);

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
            throw new FileNotFoundException($"Lua script not found: {relativePath}");
        }

        return await BuildListAsync(stackId, luaDir, cancellationToken);
    }

    // ===== Helpers =====

    private string GetLuaDir(string stackId)
    {
        if (string.IsNullOrWhiteSpace(stackId) || stackId.Contains('/') || stackId.Contains('\\') || stackId.Contains(".."))
        {
            throw new ArgumentException($"Invalid stack id: {stackId}");
        }
        return MigrationLayout.LuaScriptsDir(Path.Combine(_buildsPath, stackId));
    }

    private async Task<LuaScriptListDto> BuildListAsync(string stackId, string luaDir, CancellationToken cancellationToken)
    {
        var dto = new LuaScriptListDto { StackId = stackId, ElunaPresent = await IsElunaPresentAsync(stackId, cancellationToken) };

        if (!Directory.Exists(luaDir))
        {
            return dto;
        }

        foreach (var dir in Directory.EnumerateDirectories(luaDir, "*", SearchOption.AllDirectories))
        {
            dto.Files.Add(new LuaScriptFileDto { Path = NormalizeRelative(luaDir, dir), IsDirectory = true });
        }

        foreach (var file in Directory.EnumerateFiles(luaDir, "*", SearchOption.AllDirectories))
        {
            var size = new FileInfo(file).Length;
            dto.Files.Add(new LuaScriptFileDto { Path = NormalizeRelative(luaDir, file), Size = size });
            dto.TotalSize += size;
        }

        dto.Files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return dto;
    }

    private async Task<bool> IsElunaPresentAsync(string stackId, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (stack is null || string.IsNullOrWhiteSpace(stack.ModuleIdsJson))
        {
            return false;
        }

        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson) ?? [];
            // A Lua engine may be Eluna (any "*eluna*" module) or the AzerothCore Lua Engine
            // (mod-ale), which is an evolved Eluna fork and drives the Lua Scripts tab all the same.
            return ids.Any(id =>
                id.Contains("eluna", StringComparison.OrdinalIgnoreCase)
                || id.Equals("mod-ale", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    /// <summary>Resolves a relative path under <paramref name="root"/>, rejecting traversal.</summary>
    private static string SafeResolve(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A path is required.");
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, normalized));

        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (candidate != rootFull && !candidate.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid path: {relativePath}");
        }

        return candidate;
    }

    private static async Task<int> ExtractZipAsync(Stream zipContent, string destDir, CancellationToken cancellationToken)
    {
        using var scratch = TempWorkspace.CreateFile("lua-upload", ".zip");
        var tempFile = scratch.Path;
        try
        {
            await using (var fs = File.Create(tempFile))
            {
                await zipContent.CopyToAsync(fs, cancellationToken);
            }

            var destFull = Path.GetFullPath(destDir);
            var destWithSep = destFull.EndsWith(Path.DirectorySeparatorChar) ? destFull : destFull + Path.DirectorySeparatorChar;

            var extracted = 0;
            using var archive = ZipFile.OpenRead(tempFile);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!target.StartsWith(destWithSep, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Zip entry escapes the lua_scripts directory: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                extracted++;
            }

            if (extracted == 0)
            {
                throw new ArgumentException("The uploaded archive contained no files.");
            }

            return extracted;
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException("The uploaded file is not a valid .zip archive.");
        }
    }
}
