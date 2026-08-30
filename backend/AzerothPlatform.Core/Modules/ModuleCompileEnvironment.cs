using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Core.Modules;

/// <summary>
/// Generic compile-time helpers: extra OS packages, hidden companion checkouts, branch pins,
/// and checkout-folder aliases declared on <see cref="ModuleCompileProfile"/>.
/// </summary>
public static class ModuleCompileEnvironment
{
    public const string ExtraDepsMarker = "azeroth-platform-extra-build-deps";

    private static readonly Regex ExtraDepsBlock = new(
        $@"# {Regex.Escape(ExtraDepsMarker)}\r?\nRUN apt-get update && apt-get install[^\n]*\r?\n",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BuildStageFrom = new(
        @"^FROM\s+\S+\s+AS\s+build\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// AzerothCore's <c>dbimport</c> binary is a tool, not an app. <c>none</c> skips it and the
    /// db-import image's <c>COPY --from=build</c> then fails. <c>db-only</c> keeps that binary and
    /// still drops map/vmap/mmap extractors, which stack images do not ship.
    /// </summary>
    public const string StackToolsBuild = "db-only";

    private static readonly Regex CtoolsBuildArg = new(
        @"ARG\s+CTOOLS_BUILD\s*=\s*""?(?:all|none)""?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LegacyToolsCmakeOn = new(
        @"-DTOOLS=(?:1|""?ON""?|""?TRUE""?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QuotedFilenameInclude = new(
        @"#include\s*""([^""\\/]+)""",
        RegexOptions.Compiled);

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hh",
    };

    public static ModuleCompileProfile ProfileOf(ModuleDto? module) =>
        module?.Compile ?? ModuleCompileProfile.Empty;

    public static ModuleCompileProfile ProfileOf(IModuleInstallHook? hook) =>
        hook?.Compile ?? ModuleCompileProfile.Empty;

    public static string CheckoutFolder(string moduleId, ModuleCompileProfile? profile = null) =>
        string.IsNullOrWhiteSpace(profile?.CheckoutFolder) ? moduleId : profile.CheckoutFolder;

    public static string CheckoutFolder(ModuleDto? module, string? fallbackId = null)
    {
        var id = module?.Id ?? fallbackId ?? string.Empty;
        return CheckoutFolder(id, module?.Compile);
    }

    public static string ModuleDirectory(string modulesPath, string moduleId, ModuleCompileProfile? profile = null) =>
        Path.Combine(modulesPath, CheckoutFolder(moduleId, profile));

    public static string ModuleDirectory(string modulesPath, ModuleDto module) =>
        ModuleDirectory(modulesPath, module.Id, module.Compile);

    public static IReadOnlyList<string> ExtraAptPackagesFor(IEnumerable<ModuleDto> selected)
    {
        var packages = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in selected)
        {
            foreach (var package in ProfileOf(module).ExtraAptPackages)
            {
                if (seen.Add(package))
                {
                    packages.Add(package);
                }
            }
        }

        return packages;
    }

    public static IReadOnlyList<string> ExtraAptPackagesFor(
        IEnumerable<string> selectedIds,
        IEnumerable<ModuleDto> catalog) =>
        ExtraAptPackagesFor(Select(catalog, selectedIds));

    /// <summary>
    /// Hook packages unioned with a bounded markdown scan of each selected module checkout.
    /// Markdown may add a known apt package; it never removes one.
    /// </summary>
    public static IReadOnlyList<string> ExtraAptPackagesFor(
        IEnumerable<ModuleDto> selected,
        string? modulesPath)
    {
        var packages = ExtraAptPackagesFor(selected).ToList();
        if (string.IsNullOrWhiteSpace(modulesPath) || !Directory.Exists(modulesPath))
        {
            return packages;
        }

        var seen = new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase);
        foreach (var module in selected)
        {
            var dir = ModuleDirectory(modulesPath, module);
            foreach (var package in ModuleMarkdownDependencyScanner.ScanDirectory(dir))
            {
                if (seen.Add(package))
                {
                    packages.Add(package);
                }
            }
        }

        return packages;
    }

    public static IReadOnlyList<string> ExtraAptPackagesFor(
        IEnumerable<string> selectedIds,
        IEnumerable<ModuleDto> catalog,
        string? modulesPath) =>
        ExtraAptPackagesFor(Select(catalog, selectedIds), modulesPath);

    public static IReadOnlyList<ModuleRuntimeSidecar> RuntimeSidecarsFor(IEnumerable<ModuleDto> selected)
    {
        var sidecars = new List<ModuleRuntimeSidecar>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in selected)
        {
            foreach (var sidecar in ProfileOf(module).RuntimeSidecars)
            {
                if (string.IsNullOrWhiteSpace(sidecar.ServiceName) || !seen.Add(sidecar.ServiceName))
                {
                    continue;
                }

                sidecars.Add(sidecar);
            }
        }

        return sidecars;
    }

    public static IReadOnlyList<ModuleRuntimeSidecar> RuntimeSidecarsFor(
        IEnumerable<string> selectedIds,
        IEnumerable<ModuleDto> catalog) =>
        RuntimeSidecarsFor(Select(catalog, selectedIds));

    public static IReadOnlyList<ModuleRuntimeSidecar> RuntimeSidecarsFor(
        IEnumerable<string> selectedIds,
        IEnumerable<IModuleInstallHook> hooks) =>
        RuntimeSidecarsFor(Select(hooks, selectedIds));

    public static ModuleRuntimeSidecar? OllamaSidecarFor(IEnumerable<ModuleRuntimeSidecar> sidecars) =>
        sidecars.FirstOrDefault(item =>
            item.ServiceName.Equals(OllamaSidecar.ServiceName, StringComparison.OrdinalIgnoreCase));

    public static bool HasOllamaSidecar(IEnumerable<ModuleRuntimeSidecar> sidecars) =>
        OllamaSidecarFor(sidecars) is not null;

    public static bool HasLlmChatterBridge(IEnumerable<ModuleRuntimeSidecar> sidecars) =>
        sidecars.Any(item =>
            item.ServiceName.Equals(LlmChatterBridge.ServiceName, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<CompileCompanionModule> CompanionsFor(IEnumerable<ModuleDto> selected)
    {
        var companions = new List<CompileCompanionModule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in selected)
        {
            foreach (var companion in ProfileOf(module).Companions)
            {
                if (string.IsNullOrWhiteSpace(companion.Id) || !seen.Add(companion.Id))
                {
                    continue;
                }

                companions.Add(companion);
            }
        }

        return companions;
    }

    public static IReadOnlyList<CompileCompanionModule> CompanionsFor(
        IEnumerable<string> selectedIds,
        IEnumerable<ModuleDto> catalog) =>
        CompanionsFor(Select(catalog, selectedIds));

    public static IReadOnlyList<CompileCompanionModule> CompanionsFor(
        IEnumerable<string> selectedIds,
        IEnumerable<IModuleInstallHook> hooks) =>
        CompanionsFor(Select(hooks, selectedIds));

    /// <summary>
    /// Branch that must be checked out for <paramref name="moduleId"/> given the selected set
    /// (first matching <see cref="ModuleBranchPin"/> wins).
    /// </summary>
    public static string? RequiredBranchFor(string moduleId, IEnumerable<ModuleDto> selected)
    {
        foreach (var module in selected)
        {
            foreach (var pin in ProfileOf(module).BranchPins)
            {
                if (pin.ModuleId.Equals(moduleId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(pin.Branch))
                {
                    return pin.Branch;
                }
            }
        }

        return null;
    }

    public static string? RequiredBranchFor(
        string moduleId,
        IEnumerable<string> selectedIds,
        IEnumerable<ModuleDto> catalog) =>
        RequiredBranchFor(moduleId, Select(catalog, selectedIds));

    /// <summary>Selected module folders plus hidden compile companions that must not be deleted.</summary>
    public static IReadOnlyList<string> ModuleDirectoriesToKeep(IEnumerable<ModuleDto> selected)
    {
        var keep = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in selected)
        {
            Add(CheckoutFolder(module));
            foreach (var companion in ProfileOf(module).Companions)
            {
                Add(CheckoutFolder(companion.Id));
            }
        }

        return keep;

        void Add(string folder)
        {
            if (!string.IsNullOrWhiteSpace(folder) && seen.Add(folder))
            {
                keep.Add(folder);
            }
        }
    }

    public static IReadOnlyList<string> ModuleDirectoriesToKeep(
        IEnumerable<string> selectedIds,
        IEnumerable<ModuleDto> catalog) =>
        ModuleDirectoriesToKeep(Select(catalog, selectedIds));

    public static IReadOnlyList<string> ModuleDirectoriesToKeep(
        IEnumerable<string> selectedIds,
        IEnumerable<IModuleInstallHook> hooks) =>
        ModuleDirectoriesToKeep(Select(hooks, selectedIds));

    public static IReadOnlyList<(string LeftId, string RightId)> ConflictingPairs(IEnumerable<ModuleDto> selected)
    {
        var list = selected.ToList();
        var ids = new HashSet<string>(list.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(string LeftId, string RightId)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in list)
        {
            foreach (var otherId in ProfileOf(module).ConflictsWith)
            {
                if (!ids.Contains(otherId))
                {
                    continue;
                }

                var left = string.Compare(module.Id, otherId, StringComparison.OrdinalIgnoreCase) <= 0
                    ? module.Id
                    : otherId;
                var right = left.Equals(module.Id, StringComparison.OrdinalIgnoreCase) ? otherId : module.Id;
                var key = $"{left}|{right}";
                if (seen.Add(key))
                {
                    pairs.Add((left, right));
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// True when two Git remotes point at the same repository (ignores trailing
    /// <c>.git</c>, slashes, and case). Used when two catalog ids share a checkout folder.
    /// </summary>
    public static bool SameGitRepository(string? left, string? right)
    {
        var a = NormalizeGitRemote(left);
        var b = NormalizeGitRemote(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.Ordinal);
    }

    public static string? NormalizeGitRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var text = url.Trim();
        while (text.EndsWith('/'))
        {
            text = text[..^1];
        }

        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        return text.ToLowerInvariant();
    }

    /// <summary>
    /// Rewrites <c>#include "Header.h"</c> when this module has that file under a different
    /// casing. Linux clang is case-sensitive; Windows and some git checkouts are not.
    /// Only filename-only quoted includes are changed, and only when the header exists in
    /// this module (core headers such as <c>Player.h</c> are left alone).
    /// </summary>
    public static string? FixCaseMismatchedIncludes(string moduleDir)
    {
        if (string.IsNullOrWhiteSpace(moduleDir) || !Directory.Exists(moduleDir))
        {
            return null;
        }

        var exactNames = new HashSet<string>(StringComparer.Ordinal);
        var onDiskByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(moduleDir, "*", SearchOption.AllDirectories))
        {
            if (IsInsideGitDir(moduleDir, path))
            {
                continue;
            }

            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            exactNames.Add(name);
            onDiskByName.TryAdd(name, name);
        }

        var patchedFiles = 0;
        var rewritten = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(moduleDir, "*", SearchOption.AllDirectories))
        {
            if (IsInsideGitDir(moduleDir, path)
                || !SourceExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            var updated = QuotedFilenameInclude.Replace(text, match =>
            {
                var included = match.Groups[1].Value;
                if (exactNames.Contains(included)
                    || !onDiskByName.TryGetValue(included, out var onDisk)
                    || onDisk.Equals(included, StringComparison.Ordinal))
                {
                    return match.Value;
                }

                rewritten.Add($"{included} -> {onDisk}");
                return match.Value.Replace(included, onDisk, StringComparison.Ordinal);
            });

            if (updated.Equals(text, StringComparison.Ordinal))
            {
                continue;
            }

            File.WriteAllText(path, updated);
            patchedFiles++;
        }

        return patchedFiles == 0
            ? null
            : $"Fixed {patchedFiles} file(s) with case-mismatched includes ({string.Join(", ", rewritten)}). Linux clang is case-sensitive.";
    }

    /// <summary>
    /// Inserts (or replaces) an apt-get RUN in the AzerothCore <c>AS build</c> stage.
    /// No-op when <paramref name="packages"/> is empty; strips a previous injection in that case.
    /// Returns the original text when no build stage is found.
    /// </summary>
    public static string InjectExtraBuildPackages(string dockerfile, IReadOnlyList<string> packages)
    {
        var without = ExtraDepsBlock.Replace(dockerfile, string.Empty);
        if (packages.Count == 0)
        {
            return without;
        }

        var match = BuildStageFrom.Match(without);
        if (!match.Success)
        {
            return dockerfile;
        }

        var block =
            $"# {ExtraDepsMarker}\n" +
            "RUN apt-get update && apt-get install -y --no-install-recommends " +
            $"{string.Join(' ', packages)} && rm -rf /var/lib/apt/lists/*\n";

        var insertAt = match.Index + match.Length;
        if (insertAt < without.Length && without[insertAt] is '\r')
        {
            insertAt++;
        }

        if (insertAt < without.Length && without[insertAt] is '\n')
        {
            insertAt++;
        }

        return without[..insertAt] + block + without[insertAt..];
    }

    /// <summary>
    /// Stack images need worldserver, authserver, and dbimport. AzerothCore's Docker build stage
    /// defaults to <c>CTOOLS_BUILD=all</c>, which also compiles the map/vmap/mmap extractors into
    /// that shared stage even when the <c>ac-tools</c> image is never built. Rewrite the ARG to
    /// <see cref="StackToolsBuild"/>.
    /// </summary>
    public static string DisableExtractorTools(string dockerfile)
    {
        if (string.IsNullOrEmpty(dockerfile))
        {
            return dockerfile;
        }

        var updated = CtoolsBuildArg.Replace(dockerfile, $"ARG CTOOLS_BUILD=\"{StackToolsBuild}\"");
        return LegacyToolsCmakeOn.Replace(updated, "-DTOOLS=0");
    }

    public static ModuleDto ToModuleDto(CompileCompanionModule companion) => new()
    {
        Id = companion.Id,
        Name = companion.Name,
        Repository = companion.Repository,
        Branch = companion.Branch,
        SourceType = ModuleSource.Git,
        IsBuiltIn = true,
    };

    public static IReadOnlyDictionary<string, ModuleDto> IndexById(IEnumerable<ModuleDto> modules)
    {
        var index = new Dictionary<string, ModuleDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            if (!string.IsNullOrWhiteSpace(module.Id))
            {
                index.TryAdd(module.Id, module);
            }
        }

        return index;
    }

    private static List<ModuleDto> Select(IEnumerable<ModuleDto> catalog, IEnumerable<string> selectedIds)
    {
        var byId = IndexById(catalog);
        var selected = new List<ModuleDto>();
        foreach (var id in selectedIds)
        {
            if (byId.TryGetValue(id, out var module))
            {
                selected.Add(module);
            }
            else
            {
                selected.Add(new ModuleDto { Id = id });
            }
        }

        return selected;
    }

    private static List<ModuleDto> Select(IEnumerable<IModuleInstallHook> hooks, IEnumerable<string> selectedIds)
    {
        var byId = new Dictionary<string, IModuleInstallHook>(StringComparer.OrdinalIgnoreCase);
        foreach (var hook in hooks)
        {
            byId.TryAdd(hook.ModuleId, hook);
        }

        var selected = new List<ModuleDto>();
        foreach (var id in selectedIds)
        {
            byId.TryGetValue(id, out var hook);
            selected.Add(new ModuleDto
            {
                Id = id,
                Compile = hook?.Compile ?? ModuleCompileProfile.Empty,
            });
        }

        return selected;
    }

    private static bool IsInsideGitDir(string moduleDir, string path)
    {
        var gitDir = Path.Combine(moduleDir, ".git") + Path.DirectorySeparatorChar;
        return path.StartsWith(gitDir, StringComparison.OrdinalIgnoreCase);
    }
}
