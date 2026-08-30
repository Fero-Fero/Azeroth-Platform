using AzerothPlatform.Infrastructure.Services.ServerWideProgression;

namespace AzerothPlatform.Infrastructure.Services.Modules;

/// <summary>
/// Matches mod-playerbots <c>CalculateTotalAccountCount</c> when a manual
/// <c>AiPlayerbot.RandomBotAccountCount</c> is written (that value is the total, including addclass).
/// </summary>
internal static class PlayerbotsRandomBotAccounts
{
    public const int DefaultBotsPerAccount = 10;
    public const int DefaultAddClassAccountPoolSize = 50;

    public static int ComputeTotal(int randomBotCount, string? confContent = null)
    {
        randomBotCount = Math.Max(0, randomBotCount);
        var botsPerAccount = DefaultBotsPerAccount;
        var addClassPool = DefaultAddClassAccountPoolSize;
        var maxBots = (double)randomBotCount;

        if (!string.IsNullOrEmpty(confContent))
        {
            if (TryReadInt(confContent, "AiPlayerbot.DisableDeathKnightLogin", out var noDk) && noDk != 0)
            {
                botsPerAccount = 9;
            }

            if (TryReadInt(confContent, "AiPlayerbot.AddClassAccountPoolSize", out var pool) && pool >= 0)
            {
                addClassPool = pool;
            }

            if (TryReadInt(confContent, "AiPlayerbot.EnablePeriodicOnlineOffline", out var periodic) && periodic != 0)
            {
                var ratio = 2.0;
                if (TryReadDouble(confContent, "AiPlayerbot.PeriodicOnlineOfflineRatio", out var parsed) && parsed > 1.0)
                {
                    ratio = parsed;
                }

                maxBots *= ratio;
            }
        }

        if (botsPerAccount < 1)
        {
            botsPerAccount = DefaultBotsPerAccount;
        }

        var rndAccounts = randomBotCount == 0
            ? 0
            : (int)Math.Ceiling(maxBots / botsPerAccount);
        return rndAccounts + addClassPool;
    }

    private static bool TryReadInt(string content, string key, out int value)
    {
        value = 0;
        return ServerConfigValueEditor.TryGetValue(content, key, out var raw)
            && int.TryParse(TrimConfValue(raw), out value);
    }

    private static bool TryReadDouble(string content, string key, out double value)
    {
        value = 0;
        return ServerConfigValueEditor.TryGetValue(content, key, out var raw)
            && double.TryParse(
                TrimConfValue(raw),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }

    private static string TrimConfValue(string raw)
    {
        var trimmed = raw.Trim().Trim('"', '\'');
        var comment = trimmed.IndexOf('#');
        if (comment >= 0)
        {
            trimmed = trimmed[..comment].Trim();
        }

        return trimmed;
    }
}
