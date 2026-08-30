using AzerothPlatform.Infrastructure.Services.Patches;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Migrations;

public sealed class DbcCsvOverlayTests
{
    [Fact]
    public void DbcTableName_strips_container_folder()
    {
        var path = Path.Combine("dbc", "tweaks", "Spell.csv");
        MigrationService.DbcTableName(path).Should().Be("Spell");
    }

    [Fact]
    public void CsvNeedsLiveBaseline_is_false_when_every_csv_has_a_matching_dbc()
    {
        MigrationService.CsvNeedsLiveBaseline(
                ["Spell.csv", "Item.txt"],
                ["Spell.dbc", "Item.dbc"])
            .Should().BeFalse();
    }

    [Fact]
    public void CsvNeedsLiveBaseline_is_true_when_a_csv_has_no_matching_dbc()
    {
        MigrationService.CsvNeedsLiveBaseline(
                ["Spell.csv", "Item.csv"],
                ["Spell.dbc"])
            .Should().BeTrue();
    }

    [Fact]
    public void CsvNeedsLiveBaseline_matches_table_names_case_insensitively()
    {
        MigrationService.CsvNeedsLiveBaseline(["spell.csv"], ["SPELL.DBC"]).Should().BeFalse();
    }

    [Fact]
    public void CsvNeedsLiveBaseline_is_true_for_csv_only()
    {
        MigrationService.CsvNeedsLiveBaseline(["Spell.csv"], []).Should().BeTrue();
    }

    [Fact]
    public void Overlay_set_contains_spell_when_patch_ships_spell_dbc()
    {
        var overlay = MigrationService.DbcTableNames(["Spell.dbc", "SkillLine.dbc"]);
        overlay.Contains("Spell").Should().BeTrue();
        overlay.Contains("spell").Should().BeTrue();
        overlay.Contains("Item").Should().BeFalse();
    }
}
