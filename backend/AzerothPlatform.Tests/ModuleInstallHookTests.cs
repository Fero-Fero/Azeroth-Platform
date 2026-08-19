using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Services.Migrations;
using AzerothPlatform.Infrastructure.Services.Modules.Install;
using AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class ModuleInstallSessionTests
{
    [Fact]
    public void SetBaseDbc_is_one_shot_and_names_both_modules()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-session-" + Guid.NewGuid().ToString("N"));
        using var session = new ModuleInstallSession(root);
        session.SetBaseDbc(new SessionBaseDbc
        {
            TableName = "Spell",
            ModuleId = "mod-foo",
            BinaryPath = Path.Combine(root, "Spell.dbc")
        });

        var act = () => session.SetBaseDbc(new SessionBaseDbc
        {
            TableName = "Item",
            ModuleId = "mod-bar",
            BinaryPath = Path.Combine(root, "Item.dbc")
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*mod-bar*Item*mod-foo*Spell*");
    }

    [Fact]
    public void Dispose_clears_BaseDbc_and_deletes_extracted_scratch_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-session-" + Guid.NewGuid().ToString("N"));
        var session = new ModuleInstallSession(root);
        var extracted = Path.Combine(session.ModuleDir("mod-foo"), "extracted");
        Directory.CreateDirectory(extracted);
        File.WriteAllText(Path.Combine(extracted, "scratch.txt"), "x");
        var csvDir = Path.Combine(session.ModuleDir("mod-foo"), "csv");
        Directory.CreateDirectory(csvDir);
        File.WriteAllText(Path.Combine(csvDir, "Spell.txt"), "ID\r\n");
        session.SetBaseDbc(new SessionBaseDbc
        {
            TableName = "Spell",
            ModuleId = "mod-foo",
            BinaryPath = "x"
        });
        session.Dispose();
        session.BaseDbc.Should().BeNull();
        Directory.Exists(root).Should().BeTrue();
        Directory.Exists(extracted).Should().BeFalse();
        File.Exists(Path.Combine(root, "mod-foo", "csv", "Spell.txt")).Should().BeTrue();
        Directory.Delete(root, recursive: true);
    }
}

public sealed class DbcTrimCoalesceTests
{
    [Fact]
    public async Task Trim_drops_identical_rows_and_keeps_updates_and_inserts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "azp-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var baseline = Path.Combine(dir, "Spell.txt");
            var module = Path.Combine(dir, "module.txt");
            await File.WriteAllTextAsync(baseline, "ID,Name\r\n1,Vanilla\r\n2,KeepMe\r\n");
            await File.WriteAllTextAsync(module, "ID,Name\r\n1,Vanilla\r\n2,Changed\r\n90001,New\r\n");

            var kept = await DbcTrimHelper.TrimAsync(module, baseline);
            kept.Should().BeTrue();
            var text = await File.ReadAllTextAsync(module);
            text.Should().Contain("2,Changed");
            text.Should().Contain("90001,New");
            text.Should().NotContain("1,Vanilla");
            text.Should().EndWith("\r\n");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Trim_deletes_file_when_empty_after_diff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "azp-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var baseline = Path.Combine(dir, "Spell.txt");
            var module = Path.Combine(dir, "module.txt");
            await File.WriteAllTextAsync(baseline, "ID,Name\r\n1,Vanilla\r\n");
            await File.WriteAllTextAsync(module, "ID,Name\r\n1,Vanilla\r\n");

            var kept = await DbcTrimHelper.TrimAsync(module, baseline);
            kept.Should().BeFalse();
            File.Exists(module).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Coalesce_throws_with_both_modules_table_and_id()
    {
        var dir = Path.Combine(Path.GetTempPath(), "azp-coalesce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var aDir = Path.Combine(dir, "a");
            var bDir = Path.Combine(dir, "b");
            Directory.CreateDirectory(aDir);
            Directory.CreateDirectory(bDir);
            var a = Path.Combine(aDir, "Spell.txt");
            var b = Path.Combine(bDir, "Spell.txt");
            await File.WriteAllTextAsync(a, "ID,Name\r\n100,Foo\r\n");
            await File.WriteAllTextAsync(b, "ID,Name\r\n100,Bar\r\n");

            var act = async () => await DbcCoalesceHelper.CoalesceAsync(
            [
                ("mod-foo", a),
                ("mod-bar", b)
            ]);

            var ex = await act.Should().ThrowAsync<ModuleDbcConflictException>();
            ex.Which.ModuleA.Should().Be("mod-foo");
            ex.Which.ModuleB.Should().Be("mod-bar");
            ex.Which.Table.Should().Be("Spell");
            ex.Which.EntryId.Should().Be("100");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Coalesce_allows_identical_rows_from_two_modules()
    {
        var dir = Path.Combine(Path.GetTempPath(), "azp-coalesce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var aDir = Path.Combine(dir, "a");
            var bDir = Path.Combine(dir, "b");
            Directory.CreateDirectory(aDir);
            Directory.CreateDirectory(bDir);
            var a = Path.Combine(aDir, "Spell.txt");
            var b = Path.Combine(bDir, "Spell.txt");
            await File.WriteAllTextAsync(a, "ID,Name\r\n100,Same\r\n");
            await File.WriteAllTextAsync(b, "ID,Name\r\n100,Same\r\n");

            var result = await DbcCoalesceHelper.CoalesceAsync(
            [
                ("mod-foo", a),
                ("mod-bar", b)
            ]);
            result.Should().ContainSingle();
            result[0].CsvText.Should().Contain("100,Same");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class ModuleInstallHookRunnerTests
{
    private sealed class FakeHook : IModuleInstallHook
    {
        public FakeHook(string id) => ModuleId = id;
        public string ModuleId { get; }

        public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

        public Task<ModuleInstallContribution> InstallAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(context.Helpers.Contribution);
    }

    [Fact]
    public void Find_matches_catalog_id_and_misses_unknown()
    {
        var runner = new ModuleInstallHookRunner([new FakeHook("mod-foo")]);
        runner.Find("mod-foo").Should().NotBeNull();
        runner.Find("mod-bar").Should().BeNull();
    }

    [Fact]
    public void Duplicate_ModuleId_throws_at_construction()
    {
        var act = () => new ModuleInstallHookRunner([new FakeHook("mod-foo"), new FakeHook("mod-foo")]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*mod-foo*");
    }
}

public sealed class IndividualProgressionInstallHookTests
{
    [Fact]
    public async Task DescribeChoices_exposes_mana_and_visual_groups()
    {
        var hook = new IndividualProgressionInstallHook();
        var groups = await hook.DescribeChoicesAsync(null!, CancellationToken.None);
        groups.Should().Contain(g => g.Id == "mana-costs" && g.Kind == ModuleInstallChoiceKind.Exclusive && !g.AllowNone);
        groups.Should().Contain(g => g.Id == "visuals" && g.Kind == ModuleInstallChoiceKind.Independent);
        groups.Should().Contain(g => g.Id == "optional-sql" && g.Kind == ModuleInstallChoiceKind.Independent);
        groups.Single(g => g.Id == "mana-costs").Choices.Should().Contain(c => c.Id == "patch-s" && c.DefaultSelected);
        groups.Single(g => g.Id == "optional-sql").Choices.Should().OnlyContain(c => c.DefaultSelected);
    }
}

public sealed class PatchIndexExpansionBaselineTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("2.0")]
    [InlineData("3.0")]
    public void Expansion_baselines_are_allowed_for_swp_dbc(string raw)
    {
        PatchIndex.Parse(raw).IsExpansionBaseline.Should().BeTrue();
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("1.0.1")]
    [InlineData("2.3")]
    [InlineData("3.2")]
    public void Later_tiers_are_not_expansion_baselines(string raw)
    {
        PatchIndex.Parse(raw).IsExpansionBaseline.Should().BeFalse();
    }
}

public sealed class InstalledModulesLayoutTests
{
    [Fact]
    public void CollectCsvSources_filters_by_table_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-im-" + Guid.NewGuid().ToString("N"));
        try
        {
            var talent = Path.Combine(InstalledModulesLayout.CsvDir(root, "mod-talents"), "Talent.txt");
            var spell = Path.Combine(InstalledModulesLayout.CsvDir(root, "mod-talents"), "Spell.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(talent)!);
            File.WriteAllText(talent, "ID,Name\r\n9,NewTalent\r\n");
            File.WriteAllText(spell, "ID,Name\r\n1,Nope\r\n");
            InstalledModulesLayout.SaveManifest(root, "mod-talents", new InstalledModuleManifest { ModuleId = "mod-talents" });

            var talentOnly = InstalledModulesLayout.CollectCsvSources(root, ["mod-talents"], "Talent");
            talentOnly.Should().ContainSingle(s => s.CsvPath.EndsWith("Talent.txt", StringComparison.OrdinalIgnoreCase));
            InstalledModulesLayout.CollectCsvSources(root, ["mod-talents"], "Spell")
                .Should().ContainSingle(s => s.CsvPath.EndsWith("Spell.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class SwpDbcDestinationTests
{
    [Fact]
    public void Dbc_on_1_1_is_restricted_and_1_0_is_not()
    {
        var stackRoot = Path.Combine(Path.GetTempPath(), "azp-swpdbc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var later = Path.Combine(
                MigrationLayout.MigrationsRoot(stackRoot),
                PatchFolderNames.Format(PatchIndex.Parse("1.1"), "naxx"),
                "dbc");
            ServerWideProgressionService.IsNonBaselineSwpDbcDestination(stackRoot, later).Should().BeTrue();

            var baseline = Path.Combine(
                MigrationLayout.MigrationsRoot(stackRoot),
                PatchFolderNames.Format(new PatchIndex(1, 0, explicitSub1: true), "start"),
                "dbc");
            ServerWideProgressionService.IsNonBaselineSwpDbcDestination(stackRoot, baseline).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(stackRoot))
            {
                Directory.Delete(stackRoot, recursive: true);
            }
        }
    }
}

public sealed class PetBattleInstallHookTests
{
    [Fact]
    public async Task DescribeChoices_has_no_ui_groups()
    {
        var hook = new PetBattleInstallHook();
        var groups = await hook.DescribeChoicesAsync(null!, CancellationToken.None);
        groups.Should().BeEmpty();
    }
}

public sealed class ClanCentaurInstallHookTests
{
    [Fact]
    public async Task DescribeChoices_has_no_ui_groups()
    {
        var groups = await new ClanCentaurInstallHook().DescribeChoicesAsync(null!, CancellationToken.None);
        groups.Should().BeEmpty();
    }
}

public sealed class DelvesInstallHookTests
{
    [Fact]
    public async Task DescribeChoices_has_no_ui_groups()
    {
        var groups = await new DelvesInstallHook().DescribeChoicesAsync(null!, CancellationToken.None);
        groups.Should().BeEmpty();
    }
}
