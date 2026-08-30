namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Compile/checkout recipe for a catalog module. Declared on
/// <c>IModuleInstallHook</c> and copied onto <see cref="ModuleDto"/> when the catalog is listed.
/// </summary>
public sealed class ModuleCompileProfile
{
    public static ModuleCompileProfile Empty { get; } = new();

    /// <summary>
    /// Directory under <c>modules/</c> that AzerothCore CMake uses to generate
    /// <c>Add{folder}Scripts()</c>. Null means use the catalog module id.
    /// </summary>
    public string? CheckoutFolder { get; init; }

    public IReadOnlyList<string> ExtraAptPackages { get; init; } = [];

    public IReadOnlyList<CompileCompanionModule> Companions { get; init; } = [];

    /// <summary>Other catalog ids that cannot be selected together with this module.</summary>
    public IReadOnlyList<string> ConflictsWith { get; init; } = [];

    /// <summary>
    /// When this module is selected, pin another catalog module to a branch
    /// (wins over the operator's per-module branch override).
    /// </summary>
    public IReadOnlyList<ModuleBranchPin> BranchPins { get; init; } = [];

    /// <summary>
    /// Compose sidecars to start with the stack when this module is selected
    /// (unioned by <see cref="ModuleRuntimeSidecar.ServiceName"/>).
    /// </summary>
    public IReadOnlyList<ModuleRuntimeSidecar> RuntimeSidecars { get; init; } = [];
}

/// <summary>
/// A compose sidecar declared by a module (for example Ollama). The generator
/// keys off <see cref="ServiceName"/> rather than catalog ids.
/// </summary>
public sealed class ModuleRuntimeSidecar
{
    public string ServiceName { get; init; } = string.Empty;

    public string Image { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string ModelsVolumeName { get; init; } = string.Empty;

    public string ModelsVolumeKey { get; init; } = string.Empty;

    public IReadOnlyList<ModuleSidecarConfRewrite> ConfRewrites { get; init; } = [];
}

/// <summary>
/// Rewrites a module conf key from a localhost default to the sidecar DNS name.
/// Custom operator URLs that do not match <see cref="LocalhostValues"/> are left alone.
/// </summary>
public sealed class ModuleSidecarConfRewrite
{
    public string Key { get; init; } = string.Empty;

    public string SidecarValue { get; init; } = string.Empty;

    public IReadOnlyList<string> LocalhostValues { get; init; } = [];

    /// <summary>Optional file name such as <c>mod_ollama_chat.conf</c>; empty scans every module conf.</summary>
    public string? FileNameHint { get; init; }
}

/// <summary>Forces <see cref="ModuleId"/> onto <see cref="Branch"/> when the owning module is selected.</summary>
public sealed class ModuleBranchPin
{
    public string ModuleId { get; init; } = string.Empty;

    public string Branch { get; init; } = string.Empty;
}

/// <summary>Git checkout required to compile another selected module; not shown as an installable catalog item.</summary>
public sealed class CompileCompanionModule
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Repository { get; init; } = string.Empty;

    public string Branch { get; init; } = string.Empty;
}
