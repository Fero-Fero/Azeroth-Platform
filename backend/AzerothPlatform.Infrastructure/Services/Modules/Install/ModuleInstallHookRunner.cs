using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

public sealed class ModuleInstallHookRunner : IModuleInstallHookRunner
{
    private readonly IReadOnlyList<IModuleInstallHook> _hooks;

    public ModuleInstallHookRunner(IEnumerable<IModuleInstallHook> hooks)
    {
        var list = hooks.ToList();
        var duplicates = list
            .GroupBy(hook => hook.ModuleId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Duplicate IModuleInstallHook ModuleId values: " + string.Join(", ", duplicates));
        }

        _hooks = list;
    }

    public IReadOnlyList<IModuleInstallHook> All => _hooks;

    public IModuleInstallHook? Find(string moduleId) =>
        _hooks.FirstOrDefault(hook => string.Equals(hook.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
}
