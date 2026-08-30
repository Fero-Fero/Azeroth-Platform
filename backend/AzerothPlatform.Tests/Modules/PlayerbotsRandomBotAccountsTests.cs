using AzerothPlatform.Infrastructure.Services.Modules;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class PlayerbotsRandomBotAccountsTests
{
    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 51)]
    [InlineData(50, 55)]
    [InlineData(500, 100)]
    [InlineData(2000, 250)]
    public void ComputeTotal_uses_ten_bots_per_account_plus_default_addclass_pool(int bots, int expected)
    {
        PlayerbotsRandomBotAccounts.ComputeTotal(bots).Should().Be(expected);
    }

    [Fact]
    public void ComputeTotal_reads_addclass_pool_and_death_knight_login_from_conf()
    {
        const string conf = """
            AiPlayerbot.AddClassAccountPoolSize = 10
            AiPlayerbot.DisableDeathKnightLogin = 1
            """;

        PlayerbotsRandomBotAccounts.ComputeTotal(90, conf).Should().Be(20);
    }

    [Fact]
    public void ComputeTotal_applies_periodic_online_offline_ratio()
    {
        const string conf = """
            AiPlayerbot.EnablePeriodicOnlineOffline = 1
            AiPlayerbot.PeriodicOnlineOfflineRatio = 2.0
            AiPlayerbot.AddClassAccountPoolSize = 50
            """;

        PlayerbotsRandomBotAccounts.ComputeTotal(50, conf).Should().Be(60);
    }
}
