namespace AzerothPlatform.Infrastructure.Services;

/// <summary>Known armory page identifiers for multi-page layout (V2).</summary>
internal static class ArmoryPageIds
{
    public const string Home = "home";
    public const string Connect = "connect";
    public const string NewsList = "news-list";
    public const string Character = "character";
    public const string CharacterTalents = "character-talents";
    public const string CharacterSkills = "character-skills";
    public const string CharacterAchievements = "character-achievements";
    public const string CharacterProgression = "character-progression";
    public const string CharacterLogs = "character-logs";
    public const string Guild = "guild";
    public const string TopLogs = "top-logs";
    public const string Map = "map";

    public static readonly string[] All =
    [
        Home,
        Connect,
        NewsList,
        Character,
        CharacterTalents,
        CharacterSkills,
        CharacterAchievements,
        CharacterProgression,
        CharacterLogs,
        Guild,
        TopLogs,
        Map,
    ];
}
