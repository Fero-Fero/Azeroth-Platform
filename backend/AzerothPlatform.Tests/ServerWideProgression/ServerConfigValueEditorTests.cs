using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.ServerWideProgression;

public sealed class ServerConfigValueEditorTests
{
    private const string SampleConf = """
        # Comment line
        IndividualProgression.StartingProgression = 3
        IndividualProgression.ProgressionLimit = 3
        IndividualProgression.TbcRacesUnlockProgression = 8
        IndividualProgression.TbcRacesStartingProgression = 8
        Other.Setting = 1
        """;

    [Fact]
    public void GrepIndividualProgressionKeys_finds_all_ip_keys()
    {
        var keys = ServerConfigValueEditor.GrepIndividualProgressionKeys(SampleConf);
        keys.Should().HaveCount(4);
        keys["IndividualProgression.StartingProgression"].Should().Be("3");
        keys["IndividualProgression.ProgressionLimit"].Should().Be("3");
    }

    [Fact]
    public void SetValue_replaces_existing_key_line()
    {
        var updated = ServerConfigValueEditor.SetValue(
            SampleConf,
            "IndividualProgression.ProgressionLimit",
            "4");
        ServerConfigValueEditor.TryGetValue(updated, "IndividualProgression.ProgressionLimit", out var value)
            .Should().BeTrue();
        value.Should().Be("4");
        updated.Should().NotContain("ProgressionLimit = 3");
    }

    [Fact]
    public void SetValue_appends_missing_key()
    {
        var updated = ServerConfigValueEditor.SetValue("Other.Setting = 1", "IndividualProgression.StartingProgression", "2");
        ServerConfigValueEditor.TryGetValue(updated, "IndividualProgression.StartingProgression", out var value)
            .Should().BeTrue();
        value.Should().Be("2");
    }

    [Fact]
    public void ApplyKeyMapping_uses_discovered_key_names()
    {
        var discovered = ServerConfigValueEditor.GrepIndividualProgressionKeys(SampleConf);
        var keys = IndividualProgressionKeyNames.FromDto(new ServerWideProgressionKeyMappingDto());
        ServerConfigValueEditor.ApplyKeyMapping(keys, discovered);
        keys.StartingProgression.Should().Be("IndividualProgression.StartingProgression");
        keys.ProgressionLimit.Should().Be("IndividualProgression.ProgressionLimit");
    }

    [Fact]
    public void SetValue_applies_ollama_playerbots_chatter_disable_keys()
    {
        var content = "AiPlayerbot.Enabled = 1\nAiPlayerbot.EnableBroadcasts = 1\n";
        foreach (var (key, value) in OllamaSidecar.PlayerbotsChatterDisable)
        {
            content = ServerConfigValueEditor.SetValue(content, key, value);
        }

        foreach (var (key, value) in OllamaSidecar.PlayerbotsChatterDisable)
        {
            ServerConfigValueEditor.TryGetValue(content, key, out var written).Should().BeTrue();
            written.Should().Be(value);
        }
    }
}
