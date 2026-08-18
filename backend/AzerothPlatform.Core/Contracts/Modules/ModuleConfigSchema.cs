namespace AzerothPlatform.Core.Contracts;

public record ModuleConfigSchema(
    string ModuleId,
    string ModuleName,
    ModuleConfigOption[] Options
);

public record ModuleConfigOption(
    string Key,
    string EnvVarName,
    string DefaultValue,
    ConfigOptionType Type,
    string Description,
    string[]? EnumOptions = null
);

public enum ConfigOptionType
{
    Boolean,
    Number,
    String,
    Enum,
    StringList
}
