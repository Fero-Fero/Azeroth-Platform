using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.Patches;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Migrations;

public sealed class SwpDbcPolicyTests
{
    [Fact]
    public void AllowCsvOnLaterTiers_is_false_only_for_express()
    {
        SwpDbcPolicy.AllowCsvOnLaterTiers(ServerType.Express).Should().BeFalse();
        SwpDbcPolicy.AllowCsvOnLaterTiers(ServerType.IndividualProgression).Should().BeTrue();
        SwpDbcPolicy.AllowCsvOnLaterTiers(ServerType.Custom).Should().BeTrue();
        SwpDbcPolicy.AllowCsvOnLaterTiers(ServerType.Standard).Should().BeTrue();
        SwpDbcPolicy.AllowCsvOnLaterTiers(ServerType.Playerbots).Should().BeTrue();
    }

    [Theory]
    [InlineData("Spell.csv", true, true)]
    [InlineData("Spell.txt", true, true)]
    [InlineData("Spell.dbc", true, false)]
    [InlineData("Spell.csv", false, false)]
    [InlineData("Spell.txt", false, false)]
    [InlineData("Spell.dbc", false, false)]
    [InlineData("Spell.sql", true, false)]
    public void IsAllowedLaterTierFile_csv_only_when_enabled(string fileName, bool allowCsv, bool expected)
    {
        SwpDbcPolicy.IsAllowedLaterTierFile(fileName, allowCsv).Should().Be(expected);
    }

    [Fact]
    public void SkipLog_distinguishes_binary_dbc()
    {
        SwpDbcPolicy.SkipLog("Spell.dbc").Should().Contain("binary DBC");
        SwpDbcPolicy.SkipLog("Spell.csv").Should().NotContain("binary");
        SwpDbcPolicy.SkipLog("Spell.txt").Should().NotContain("binary");
    }
}
