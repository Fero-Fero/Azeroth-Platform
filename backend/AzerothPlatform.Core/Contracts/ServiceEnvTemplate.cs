namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Declares the environment variables a single stack service (worldserver, authserver, armory,
/// client, …) accepts. Environment variables are inherently per-container, so each service exposes
/// its own template of supported variables (with types/defaults/descriptions) that admins can fill
/// out, plus a free-form custom escape hatch handled by the UI. Mirrors <see cref="ModuleConfigSchema"/>
/// so the same schema-driven form UI can render both.
/// </summary>
public record ServiceEnvTemplate(
    string ServiceId,
    string ServiceName,
    string Description,
    ServiceEnvOption[] Options
);

/// <summary>A single environment variable a service accepts. Shares the shape of <see cref="ModuleConfigOption"/>.</summary>
public record ServiceEnvOption(
    string Key,
    string EnvVarName,
    string DefaultValue,
    ConfigOptionType Type,
    string Description,
    string[]? EnumOptions = null
);
