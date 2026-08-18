namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Type of AzerothCore server variant. Each value maps to a core repository/branch and module
/// visibility in <c>Configuration/ServerTypes/</c>. Add a new enum value and a matching catalog file
/// to introduce a new variant.
/// </summary>
public enum ServerType
{
    /// <summary>
    /// Standard AzerothCore server (official azerothcore/azerothcore-wotlk).
    /// </summary>
    Standard,

    /// <summary>
    /// AzerothCore with the Playerbots module integrated (mod-playerbots fork).
    /// </summary>
    Playerbots,

    /// <summary>
    /// Grimfeather fork tailored for the Individual Progression module, simulating expansion/tier
    /// progression. Requires the custom fork for the module to function.
    /// </summary>
    IndividualProgression,

    /// <summary>
    /// AzerothCore with NPCBots integrated (trickerer/AzerothCore-wotlk-with-NPCBots fork).
    /// </summary>
    NpcBots,

    /// <summary>
    /// User-supplied fork: the core repository URL and branch are provided at stack-creation time
    /// (see <see cref="StackConfigurationDto.CustomFork"/>) rather than resolved from the catalog.
    /// </summary>
    Custom
}
