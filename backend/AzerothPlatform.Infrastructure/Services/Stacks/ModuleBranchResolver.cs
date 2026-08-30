using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services;

namespace AzerothPlatform.Infrastructure.Services.Stacks;

internal static class ModuleBranchResolver
{
    public static Dictionary<string, string> Parse(string? json)
    {
        var parsed = string.IsNullOrWhiteSpace(json)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parsed is null)
        {
            return result;
        }

        foreach (var (key, value) in parsed)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                result[key] = value.Trim();
            }
        }

        return result;
    }

    public static string Resolve(
        ModuleDto module,
        IReadOnlyDictionary<string, string>? overrides,
        IEnumerable<ModuleDto>? selectedModules = null)
    {
        var required = ModuleCompileEnvironment.RequiredBranchFor(module.Id, selectedModules ?? []);
        if (!string.IsNullOrWhiteSpace(required))
        {
            return ModuleCatalogService.ValidateGitRef(required);
        }

        if (overrides is not null
            && overrides.TryGetValue(module.Id, out var branch)
            && !string.IsNullOrWhiteSpace(branch))
        {
            return ModuleCatalogService.ValidateGitRef(branch);
        }

        return string.IsNullOrWhiteSpace(module.Branch) ? "master" : module.Branch;
    }

    public static string Resolve(
        ModuleDto module,
        IReadOnlyDictionary<string, string>? overrides,
        IEnumerable<string> selectedIds,
        IEnumerable<ModuleDto> catalog) =>
        Resolve(module, overrides, Select(catalog, selectedIds));

    public static ModuleDto WithBranch(ModuleDto module, string branch) => new()
    {
        Id = module.Id,
        Name = module.Name,
        Description = module.Description,
        Repository = module.Repository,
        Branch = branch,
        SourceType = module.SourceType,
        IsBuiltIn = module.IsBuiltIn,
        Recommended = module.Recommended,
        RequiredModuleIds = module.RequiredModuleIds,
        Compile = module.Compile,
    };

    private static List<ModuleDto> Select(IEnumerable<ModuleDto> catalog, IEnumerable<string> selectedIds)
    {
        var byId = ModuleCompileEnvironment.IndexById(catalog);
        var selected = new List<ModuleDto>();
        foreach (var id in selectedIds)
        {
            if (byId.TryGetValue(id, out var module))
            {
                selected.Add(module);
            }
        }

        return selected;
    }
}
