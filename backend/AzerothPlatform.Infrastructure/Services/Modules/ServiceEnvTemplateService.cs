using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Static, curated per-service environment-variable templates. These describe the safe-to-tweak
/// variables each stack service accepts; anything not listed can still be added via the UI's custom
/// escape hatch. Managed variables (DB credentials, ports, secrets, container paths) are intentionally
/// omitted so operators can't break connectivity from here - the override generator re-forces those.
/// </summary>
public sealed class ServiceEnvTemplateService : IServiceEnvTemplateService
{
    /// <summary>Canonical service ids used as the per-service env buckets everywhere.</summary>
    public const string Worldserver = "worldserver";
    public const string Authserver = "authserver";
    public const string Armory = "armory";
    public const string Client = "client";

    private static readonly IReadOnlyList<ServiceEnvTemplate> Templates = new[]
    {
        new ServiceEnvTemplate(
            Worldserver,
            "World Server",
            "AzerothCore worldserver.conf overrides (AC_* environment variables). These tune gameplay rates, level caps and the message of the day.",
            new[]
            {
                new ServiceEnvOption("Message of the Day", "AC_MOTD", "Welcome to AzerothCore", ConfigOptionType.String, "Login message shown to players."),
                new ServiceEnvOption("Game Type", "AC_GAME_TYPE", "0", ConfigOptionType.Enum, "Realm type flag written to the realm list.", new[] { "0 = Normal", "1 = PvP", "4 = FFA PvP", "6 = RP", "8 = RP PvP" }),
                new ServiceEnvOption("Max Player Level", "AC_MAX_PLAYER_LEVEL", "80", ConfigOptionType.Number, "Level cap (1–80 on WotLK)."),
                new ServiceEnvOption("Start Player Level", "AC_START_PLAYER_LEVEL", "1", ConfigOptionType.Number, "Level newly created characters start at."),
                new ServiceEnvOption("Start Player Money", "AC_START_PLAYER_MONEY", "0", ConfigOptionType.Number, "Copper granted to new characters."),
                new ServiceEnvOption("XP Kill Rate", "AC_RATE_XP_KILL", "1", ConfigOptionType.Number, "Experience multiplier for kills."),
                new ServiceEnvOption("XP Quest Rate", "AC_RATE_XP_QUEST", "1", ConfigOptionType.Number, "Experience multiplier for quests."),
                new ServiceEnvOption("Money Drop Rate", "AC_RATE_DROP_MONEY", "1", ConfigOptionType.Number, "Money drop multiplier."),
                new ServiceEnvOption("Honor Rate", "AC_RATE_HONOR", "1", ConfigOptionType.Number, "Honor gain multiplier."),
                new ServiceEnvOption("Reputation Rate", "AC_RATE_REPUTATION_GAIN", "1", ConfigOptionType.Number, "Reputation gain multiplier."),
                new ServiceEnvOption("Save Interval (ms)", "AC_PLAYER_SAVE_INTERVAL", "900000", ConfigOptionType.Number, "How often player state is persisted, in milliseconds."),
                new ServiceEnvOption("Two-Side Interaction (chat)", "AC_ALLOW_TWO_SIDE_INTERACTION_CHAT", "0", ConfigOptionType.Boolean, "Allow Horde/Alliance to chat with each other."),
            }),

        new ServiceEnvTemplate(
            Authserver,
            "Auth Server",
            "AzerothCore authserver.conf overrides (AC_* environment variables). These govern the login server's brute-force protection.",
            new[]
            {
                new ServiceEnvOption("Wrong Password Max Count", "AC_WRONG_PASS_MAX_COUNT", "0", ConfigOptionType.Number, "Failed logins before action is taken (0 disables)."),
                new ServiceEnvOption("Wrong Password Ban Time (s)", "AC_WRONG_PASS_BAN_TIME", "600", ConfigOptionType.Number, "Ban duration in seconds after too many failures (0 = permanent)."),
                new ServiceEnvOption("Wrong Password Ban Type", "AC_WRONG_PASS_BAN_TYPE", "0", ConfigOptionType.Enum, "What to ban after repeated failures.", new[] { "0 = Ban IP", "1 = Ban Account" }),
            }),

        new ServiceEnvTemplate(
            Armory,
            "Armory (Website)",
            "frontend-armory overrides (ACORE_ARMORY_* environment variables). Database credentials, realm wiring and the session secret are managed automatically and cannot be overridden here.",
            new[]
            {
                new ServiceEnvOption("Website Name", "ACORE_ARMORY_WEBSITE_NAME", "", ConfigOptionType.String, "Site title shown in the armory header. Blank uses the realm name."),
                new ServiceEnvOption("Hide Game Masters", "ACORE_ARMORY_HIDE_GAME_MASTERS", "0", ConfigOptionType.Boolean, "Hide GM characters from listings and search."),
                new ServiceEnvOption("Transmog Module", "ACORE_ARMORY_TRANSMOG_MODULE", "0", ConfigOptionType.Boolean, "Enable transmogrification data in the armory (requires the transmog module)."),
                new ServiceEnvOption("Load DBCs", "ACORE_ARMORY_LOAD_DBCS", "1", ConfigOptionType.Boolean, "Load DBC data for richer item/spell tooltips."),
                new ServiceEnvOption("Use ZAM CDN", "ACORE_ARMORY_USE_ZAM_CDN", "0", ConfigOptionType.Boolean, "Fetch icons/models from the ZAM CDN instead of local assets."),
                new ServiceEnvOption("Azeroth World Map", "ACORE_ARMORY_WORLD_MAP_MODULE", "1", ConfigOptionType.Boolean, "Show the live Azeroth world map with online player positions and link it in the armory navigation. When off, the map routes and nav button are hidden."),
                new ServiceEnvOption("Allow Registration", "ACORE_ARMORY_ACCOUNTS__ALLOW_REGISTRATION", "1", ConfigOptionType.Boolean, "Allow players to create game accounts from the armory."),
                new ServiceEnvOption("Min Password Length", "ACORE_ARMORY_ACCOUNTS__MIN_PASSWORD_LENGTH", "4", ConfigOptionType.Number, "Minimum account password length."),
                new ServiceEnvOption("Max Password Length", "ACORE_ARMORY_ACCOUNTS__MAX_PASSWORD_LENGTH", "16", ConfigOptionType.Number, "Maximum account password length."),
                new ServiceEnvOption("Session Hours", "ACORE_ARMORY_ACCOUNTS__SESSION_HOURS", "24", ConfigOptionType.Number, "How long a player login session stays valid, in hours."),
                new ServiceEnvOption("DB Query Timeout (ms)", "ACORE_ARMORY_DB_QUERY_TIMEOUT", "10000", ConfigOptionType.Number, "Database query timeout in milliseconds."),
            }),

        new ServiceEnvTemplate(
            Client,
            "Client File Server",
            "azeroth-platform-client overrides. The base/overlay/cache paths, managed prefixes and auth token are managed automatically; use custom variables only for advanced tuning.",
            Array.Empty<ServiceEnvOption>()),
    };

    public IReadOnlyList<ServiceEnvTemplate> GetTemplates() => Templates;
}
