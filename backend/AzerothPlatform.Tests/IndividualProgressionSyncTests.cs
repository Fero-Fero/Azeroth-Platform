using System.IO.Compression;
using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.IndividualProgression;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class IndividualProgressionHeaderParserTests
{
    [Fact]
    public void ParseHeader_reads_progression_enum_entries()
    {
        const string header = """
            enum ProgressionState
            {
                PROGRESSION_START = 0,
                PROGRESSION_MOLTEN_CORE = 1,
                PROGRESSION_ONYXIA = 2,
            };
            """;

        var parsed = InvokeParseHeader(header);
        parsed.Should().HaveCount(3);
        parsed[0].State.Should().Be(0);
        parsed[0].Slug.Should().Be("START");
        parsed[1].Slug.Should().Be("MOLTEN_CORE");
    }

    private static List<IndividualProgressionHeaderParser.ParsedState> InvokeParseHeader(string content)
    {
        var method = typeof(IndividualProgressionHeaderParser)
            .GetMethod("ParseHeader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (List<IndividualProgressionHeaderParser.ParsedState>)method!.Invoke(null, [content])!;
    }
}

public sealed class IndividualProgressionSyncLogicTests
{
    [Theory]
    [InlineData(0, "classic", false, "0")]
    [InlineData(1, "classic", true, null)]
    [InlineData(8, "tbc", true, "1")]
    [InlineData(14, "wotlk", true, "2")]
    public void ResolveExpansionForPatch_sets_boundaries(int state, string expansion, bool increments, string? expectedExpansion)
    {
        var metadata = new PatchProgressionMetadataDto
        {
            State = state,
            Slug = "TEST",
            Expansion = expansion,
            IncrementsProgression = increments,
        };

        var result = InvokeResolveExpansion(metadata);
        result.Should().Be(expectedExpansion);
    }

    [Fact]
    public void IncrementConfigValue_increments_parsed_integer()
    {
        var settings = new IndividualProgressionSettingsDto
        {
            Values = { ["IndividualProgression.ProgressionLimit"] = "3" },
        };

        var result = InvokeIncrement(settings, "IndividualProgression.ProgressionLimit");
        result.Should().Be("4");
    }

    private static string? InvokeResolveExpansion(PatchProgressionMetadataDto metadata)
    {
        var method = typeof(IndividualProgressionSyncService)
            .GetMethod("ResolveExpansionForPatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string?)method!.Invoke(null, [metadata]);
    }

    private static string InvokeIncrement(IndividualProgressionSettingsDto settings, string key)
    {
        var method = typeof(IndividualProgressionSyncService)
            .GetMethod("IncrementConfigValue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, [settings, key])!;
    }
}
