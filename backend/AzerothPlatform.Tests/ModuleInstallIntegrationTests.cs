using System.IO.Compression;
using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Migrations;
using AzerothPlatform.Infrastructure.Services.Modules.Install;
using AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests;

/// <summary>
/// Fixture-based extra-data pipeline tests. DBC "binaries" are CSV text with a .dbc extension and
/// MPQs are zip archives, so CI never runs WDBX, mpqtool, or wowgaming/client-data.
/// </summary>
public sealed class ModuleInstallIntegrationTests
{
    private const string VanillaSkillLine = "ID,Name\r\n1,Vanilla\r\n2,AlsoVanilla\r\n";
    private const string VanillaSpell = "ID,Mana\r\n1,100\r\n2,200\r\n";

    [Fact]
    public async Task ExtractArchive_then_ExtractDbcByName_is_case_insensitive_and_does_not_export_other_tables()
    {
        using var fx = new Fixture();
        var package = fx.ModulePackage("mod-skill-tweaks");
        WriteZip(
            Path.Combine(package, "optional", "dbc.7z"),
            new Dictionary<string, string>
            {
                ["SkillLine.dbc"] = VanillaSkillLine + "90001,Tweaked\r\n",
                ["Item.dbc"] = "ID,Name\r\n1,Sword\r\n",
            });

        var (session, helpers) = fx.Helpers("mod-skill-tweaks", package);
        using (session)
        {
            await helpers.ExtractArchive("optional/dbc.7z");
            await helpers.ExtractDbcByName("skillline");

            var csvDir = Path.Combine(session.ModuleDir("mod-skill-tweaks"), "csv");
            File.Exists(Path.Combine(csvDir, "SkillLine.txt")).Should().BeTrue();
            File.Exists(Path.Combine(csvDir, "Item.txt")).Should().BeFalse();
            fx.Wdbx.Exports.Should().ContainSingle(e => e.EndsWith("SkillLine.dbc", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task ExtractDbcsFromMpq_filters_by_table_name()
    {
        using var fx = new Fixture();
        var package = fx.ModulePackage("mod-spell-pack");
        WriteZip(
            Path.Combine(package, "optional", "patch-V.mpq"),
            new Dictionary<string, string>
            {
                ["DBFilesClient/Spell.dbc"] = VanillaSpell + "90001,50\r\n",
                ["DBFilesClient/Item.dbc"] = "ID,Name\r\n8,Ring\r\n",
            });

        var (session, helpers) = fx.Helpers("mod-spell-pack", package);
        using (session)
        {
            await helpers.ExtractDbcsFromMpq("optional/patch-V.mpq", "Spell");

            fx.Wdbx.MpqExtracts.Should().ContainSingle()
                .Which.Filter.Should().Be("Spell");
            var csvDir = Path.Combine(session.ModuleDir("mod-spell-pack"), "csv");
            File.Exists(Path.Combine(csvDir, "Spell.txt")).Should().BeTrue();
            File.Exists(Path.Combine(csvDir, "Item.txt")).Should().BeFalse();
            (await File.ReadAllTextAsync(Path.Combine(csvDir, "Spell.txt"))).Should().Contain("90001,50");
        }
    }

    [Fact]
    public async Task ExtractDbcsFromMpq_without_filter_exports_every_dbc_in_the_archive()
    {
        using var fx = new Fixture();
        var package = fx.ModulePackage("mod-spell-pack");
        WriteZip(
            Path.Combine(package, "patch.mpq"),
            new Dictionary<string, string>
            {
                ["Spell.dbc"] = VanillaSpell,
                ["SkillLine.dbc"] = VanillaSkillLine,
            });

        var (session, helpers) = fx.Helpers("mod-spell-pack", package);
        using (session)
        {
            await helpers.ExtractDbcsFromMpq("patch.mpq");
            var csvDir = Path.Combine(session.ModuleDir("mod-spell-pack"), "csv");
            File.Exists(Path.Combine(csvDir, "Spell.txt")).Should().BeTrue();
            File.Exists(Path.Combine(csvDir, "SkillLine.txt")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Two_modules_trim_coalesce_and_import_distinct_SkillLine_rows()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        SeedServerDbc(fx, "SkillLine.dbc", VanillaSkillLine);

        WriteDeltaModule(fx, "mod-alpha", "SkillLine.dbc", VanillaSkillLine + "90001,Alpha\r\n");
        WriteDeltaModule(fx, "mod-beta", "SkillLine.dbc", VanillaSkillLine + "90002,Beta\r\n");

        var orchestrator = fx.CreateOrchestrator(
            ["mod-alpha", "mod-beta"],
            new ArchiveDeltaHook("mod-alpha", "SkillLine"),
            new ArchiveDeltaHook("mod-beta", "SkillLine"));

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        var imported = await File.ReadAllTextAsync(Path.Combine(fx.StackRoot, "server_dbc", "SkillLine.dbc"));
        imported.Should().Contain("1,Vanilla");
        imported.Should().Contain("2,AlsoVanilla");
        imported.Should().Contain("90001,Alpha");
        imported.Should().Contain("90002,Beta");
        fx.PushedDbc.Should().Contain("SkillLine.dbc");
        fx.RebuiltPatchD.Should().BeTrue();
        fx.Wdbx.Imports.Should().ContainSingle(i => i.Dbc.EndsWith("SkillLine.dbc", StringComparison.OrdinalIgnoreCase));
        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", "mod-alpha", "csv", "SkillLine.txt")).Should().BeTrue();
        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", "mod-beta", "csv", "SkillLine.txt")).Should().BeTrue();
        orchestrator.GetStackStatus(fx.StackId).Deposited.Should().BeTrue();
    }

    [Fact]
    public async Task Two_modules_that_disagree_on_the_same_SkillLine_id_abort_before_deposit()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        SeedServerDbc(fx, "SkillLine.dbc", VanillaSkillLine);

        WriteDeltaModule(fx, "mod-alpha", "SkillLine.dbc", VanillaSkillLine + "100,FromAlpha\r\n");
        WriteDeltaModule(fx, "mod-beta", "SkillLine.dbc", VanillaSkillLine + "100,FromBeta\r\n");

        var orchestrator = fx.CreateOrchestrator(
            ["mod-alpha", "mod-beta"],
            new ArchiveDeltaHook("mod-alpha", "SkillLine"),
            new ArchiveDeltaHook("mod-beta", "SkillLine"));

        var act = () => orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });
        var ex = await act.Should().ThrowAsync<ModuleDbcConflictException>();
        ex.Which.Table.Should().Be("SkillLine");
        ex.Which.EntryId.Should().Be("100");
        fx.RebuiltPatchD.Should().BeFalse();
        fx.Wdbx.Imports.Should().BeEmpty();
    }

    [Fact]
    public async Task IndividualProgression_patch_v_extracts_Spell_from_mpq_sets_base_trims_deltas_and_strips_dbc_mpq()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        fx.WriteStoreCsv("SkillLineAbility", "ID,Name\r\n1,Vanilla\r\n");
        fx.WriteStoreCsv("SkillRaceClassInfo", "ID,Name\r\n1,Vanilla\r\n");
        fx.WriteStoreCsv("SpellItemEnchantment", "ID,Name\r\n1,Vanilla\r\n");
        fx.WriteStoreCsv("Spell", VanillaSpell);
        SeedServerDbc(fx, "SkillLine.dbc", VanillaSkillLine);
        SeedServerDbc(fx, "Spell.dbc", VanillaSpell);

        var package = fx.StackModule(IndividualProgressionInstallHook.CatalogId);
        WriteZip(
            Path.Combine(package, "optional", "dbc.7z"),
            new Dictionary<string, string>
            {
                ["SkillLine.dbc"] = VanillaSkillLine + "90001,IPSkill\r\n",
                ["SkillLineAbility.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
                ["SkillRaceClassInfo.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
                ["SpellItemEnchantment.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
            });
        WriteZip(
            Path.Combine(package, "optional", "patch-V.7z"),
            new Dictionary<string, byte[]>
            {
                ["patch-V.mpq"] = ZipBytes(new Dictionary<string, string>
                {
                    ["Spell.dbc"] = "ID,Mana\r\n1,50\r\n2,80\r\n",
                }),
            });
        WriteZip(
            Path.Combine(package, "optional", "patch-J.mpq"),
            new Dictionary<string, string>
            {
                ["Interface/GLUES/login.blp"] = "not-a-dbc",
            });
        Directory.CreateDirectory(Path.Combine(package, "optional", "sql", "world"));
        await File.WriteAllTextAsync(
            Path.Combine(package, "optional", "sql", "world", "zz_optional_ammo_stack_size.sql"),
            "UPDATE item_template SET stackable = 1000 WHERE entry = 2512;\n");

        var spellTweaks = fx.StackModule("mod-spell-tweaks");
        WriteZip(
            Path.Combine(spellTweaks, "optional", "dbc.7z"),
            new Dictionary<string, string>
            {
                ["Spell.dbc"] = "ID,Mana\r\n1,50\r\n2,80\r\n90002,10\r\n",
            });

        var orchestrator = fx.CreateOrchestrator(
            [IndividualProgressionInstallHook.CatalogId, "mod-spell-tweaks"],
            new IndividualProgressionInstallHook(),
            new ArchiveDeltaHook("mod-spell-tweaks", "Spell"));

        await orchestrator.ApplyAsync(
            fx.StackId,
            new ApplyModuleExtraDataRequest
            {
                SelectionsByModuleId =
                {
                    [IndividualProgressionInstallHook.CatalogId] = new ModuleInstallSelections
                    {
                        Groups =
                        {
                            ["mana-costs"] = ["patch-v"],
                            ["visuals"] = ["login"],
                        }
                    }
                }
            },
            _ => { });

        fx.Wdbx.MpqExtracts.Should().Contain(e =>
            e.Mpq.EndsWith("patch-V.mpq", StringComparison.OrdinalIgnoreCase) && e.Filter == "Spell");

        var liveSpell = await File.ReadAllTextAsync(Path.Combine(fx.StackRoot, "server_dbc", "Spell.dbc"));
        liveSpell.Should().Contain("1,50");
        liveSpell.Should().Contain("2,80");
        liveSpell.Should().Contain("90002,10");
        liveSpell.Should().NotContain("1,100");

        var liveSkill = await File.ReadAllTextAsync(Path.Combine(fx.StackRoot, "server_dbc", "SkillLine.dbc"));
        liveSkill.Should().Contain("1,Vanilla");
        liveSkill.Should().Contain("90001,IPSkill");

        fx.SqlByDatabase.Should().ContainKey("acore_world");
        fx.SqlByDatabase["acore_world"].Should().Contain(p => p.EndsWith("zz_optional_ammo_stack_size.sql"));
        fx.SavedWorldserverConf.Should().Contain("PlayerSettings.EnablePlayerSettings = 1");
        fx.SavedWorldserverConf.Should().Contain("DBC.EnforceItemAttributes = 0");

        fx.PublishedMpqs.Should().Contain(p => p.Contains("patch-J", StringComparison.OrdinalIgnoreCase));
        fx.PublishedMpqs.Should().NotContain(p => p.Contains("patch-V", StringComparison.OrdinalIgnoreCase));

        var moduleData = Path.Combine(fx.StackRoot, "InstalledModules", IndividualProgressionInstallHook.CatalogId);
        File.Exists(Path.Combine(moduleData, "selections.json")).Should().BeTrue();
        File.Exists(Path.Combine(moduleData, "sql", "world", "zz_optional_ammo_stack_size.sql")).Should().BeTrue();
    }

    [Fact]
    public async Task IndividualProgression_without_mana_choice_does_not_replace_live_Spell()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        fx.WriteStoreCsv("SkillLineAbility", "ID,Name\r\n1,Vanilla\r\n");
        fx.WriteStoreCsv("SkillRaceClassInfo", "ID,Name\r\n1,Vanilla\r\n");
        fx.WriteStoreCsv("SpellItemEnchantment", "ID,Name\r\n1,Vanilla\r\n");
        SeedServerDbc(fx, "SkillLine.dbc", VanillaSkillLine);
        SeedServerDbc(fx, "Spell.dbc", VanillaSpell);

        var package = fx.StackModule(IndividualProgressionInstallHook.CatalogId);
        WriteZip(
            Path.Combine(package, "optional", "dbc.7z"),
            new Dictionary<string, string>
            {
                ["SkillLine.dbc"] = VanillaSkillLine + "90001,IPSkill\r\n",
                ["SkillLineAbility.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
                ["SkillRaceClassInfo.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
                ["SpellItemEnchantment.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
            });

        var orchestrator = fx.CreateOrchestrator(
            [IndividualProgressionInstallHook.CatalogId],
            new IndividualProgressionInstallHook());

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest
        {
            IpContentMode = IpContentMode.Standard,
            SelectionsByModuleId =
            {
                [IndividualProgressionInstallHook.CatalogId] = new ModuleInstallSelections
                {
                    Groups = { ["optional-sql"] = [] }
                }
            }
        }, _ => { });

        fx.Wdbx.MpqExtracts.Should().BeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(fx.StackRoot, "server_dbc", "Spell.dbc")))
            .Should().Be(VanillaSpell);
        fx.PushedDbc.Should().NotContain("Spell.dbc");
        fx.PushedDbc.Should().Contain("SkillLine.dbc");
    }

    [Fact]
    public async Task PetBattle_installs_only_the_stock_addon_tree()
    {
        using var fx = new Fixture();
        var package = fx.StackModule(PetBattleInstallHook.CatalogId);
        WriteAddon(package, Path.Combine("Interface", "AddOns", "PetBattleUI"), "STOCK");
        WriteAddon(
            package,
            Path.Combine("Interface compatible whit DragonUI Addon", "AddOns", "PetBattleUI"),
            "DRAGON");

        var orchestrator = fx.CreateOrchestrator(
            [PetBattleInstallHook.CatalogId],
            new PetBattleInstallHook());

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        fx.InstalledAddons.Should().ContainSingle();
        fx.InstalledAddons[0].Folder.Should().Be("PetBattleUI");
        fx.InstalledAddonTocs.Should().ContainSingle(t => t.Contains("STOCK"));
        fx.InstalledAddonTocs.Should().NotContain(t => t.Contains("DRAGON"));
    }

    [Fact]
    public async Task GuildLevels_installs_named_addon_and_lua_extension()
    {
        using var fx = new Fixture();
        var package = fx.StackModule(GuildLevelsInstallHook.CatalogId);
        WriteAddon(package, Path.Combine("client_addon", "GuildLevels"), "GUILD");
        var extDir = Path.Combine(package, "lua", "extensions", "guild_levels");
        Directory.CreateDirectory(extDir);
        File.WriteAllText(Path.Combine(extDir, "guild_levels.ext"), "-- guild levels\n");

        var orchestrator = fx.CreateOrchestrator(
            [GuildLevelsInstallHook.CatalogId],
            new GuildLevelsInstallHook());

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        fx.InstalledAddons.Should().ContainSingle(a => a.Folder == "GuildLevels");
        File.ReadAllText(Path.Combine(fx.StackRoot, "lua_scripts", "extensions", "guild_levels", "guild_levels.ext"))
            .Should().Contain("guild levels");
    }

    [Fact]
    public async Task BlackMarket_installs_named_addon_and_lua_scripts()
    {
        using var fx = new Fixture();
        var package = fx.StackModule(BlackMarketAuctionHouseInstallHook.CatalogId);
        WriteAddon(package, Path.Combine("Client Files", "AddOns", "BlackMarketUI"), "BMAH");
        var luaDir = Path.Combine(package, "Server Files", "lua_scripts");
        Directory.CreateDirectory(luaDir);
        File.WriteAllText(Path.Combine(luaDir, "bmah_server.lua"), "-- bmah\n");

        var orchestrator = fx.CreateOrchestrator(
            [BlackMarketAuctionHouseInstallHook.CatalogId],
            new BlackMarketAuctionHouseInstallHook());

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        fx.InstalledAddons.Should().ContainSingle(a => a.Folder == "BlackMarketUI");
        File.ReadAllText(Path.Combine(fx.StackRoot, "lua_scripts", "bmah_server.lua"))
            .Should().Contain("bmah");
    }

    [Fact]
    public async Task Aio_installs_server_lua_tree()
    {
        using var fx = new Fixture();
        var package = fx.StackModule(AioInstallHook.CatalogId);
        var luaDir = Path.Combine(package, "AIO_Server");
        Directory.CreateDirectory(luaDir);
        File.WriteAllText(Path.Combine(luaDir, "AIO.lua"), "-- aio\n");
        Directory.CreateDirectory(Path.Combine(luaDir, "Dep_Smallfolk"));
        File.WriteAllText(Path.Combine(luaDir, "Dep_Smallfolk", "smallfolk.lua"), "-- dep\n");

        var orchestrator = fx.CreateOrchestrator(
            [AioInstallHook.CatalogId],
            new AioInstallHook());

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        File.ReadAllText(Path.Combine(fx.StackRoot, "lua_scripts", "AIO.lua")).Should().Contain("aio");
        File.ReadAllText(Path.Combine(fx.StackRoot, "lua_scripts", "Dep_Smallfolk", "smallfolk.lua"))
            .Should().Contain("dep");
    }

    [Fact]
    public async Task IpChallenge_applies_named_characters_sql()
    {
        using var fx = new Fixture();
        var package = fx.StackModule(IpChallengeSystemInstallHook.CatalogId);
        var sqlDir = Path.Combine(package, "sql", "characters");
        Directory.CreateDirectory(sqlDir);
        File.WriteAllText(Path.Combine(sqlDir, "001_create_ip_challenge_runs.sql"), "CREATE TABLE ip_challenge_runs;\n");
        File.WriteAllText(Path.Combine(sqlDir, "002_create_ip_permadeath.sql"), "CREATE TABLE ip_permadeath;\n");

        var orchestrator = fx.CreateOrchestrator(
            [IpChallengeSystemInstallHook.CatalogId],
            new IpChallengeSystemInstallHook());

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        fx.SqlApplyOrder.Should().Equal("acore_characters");
        fx.SqlByDatabase["acore_characters"].Should().HaveCount(2);
    }

    [Fact]
    public async Task ClanCentaur_imports_faction_csv_and_named_world_sql()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("Faction", "ID,Name\r\n1,Alliance\r\n92,GelkisClanCentaur\r\n");
        SeedServerDbc(fx, "Faction.dbc", "ID,Name\r\n1,Alliance\r\n92,GelkisClanCentaur\r\n");

        var package = fx.StackModule(ClanCentaurInstallHook.CatalogId);
        WriteSql(package, Path.Combine("DBClientFiles", "Faction.csv"), "ID,Name\r\n92,Gelkis Clan Centaur\r\n93,Magram Clan Centaur\r\n");
        WriteSql(package, Path.Combine("data", "sql", "world", "base", "ClanCentaur_Items.sql"), "INSERT INTO item_template;\n");
        WriteSql(package, Path.Combine("data", "sql", "world", "base", "ClanCentaur_NPCVendors.sql"), "INSERT INTO npc_vendor;\n");

        var orchestrator = fx.CreateOrchestrator(
            [ClanCentaurInstallHook.CatalogId],
            new ClanCentaurInstallHook());

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", ClanCentaurInstallHook.CatalogId, "csv", "Faction.txt"))
            .Should().BeTrue();
        fx.PushedDbc.Should().Contain("Faction.dbc");
        fx.SqlApplyOrder.Should().Equal("acore_world");
        fx.SqlByDatabase["acore_world"].Select(Path.GetFileName).Should().BeEquivalentTo(
            "ClanCentaur_Items.sql",
            "ClanCentaur_NPCVendors.sql");
    }

    [Fact]
    public async Task Delves_packs_overlay_mpq_and_publishes_maps_csv_and_lua()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("AreaTable", "ID,Name\r\n1,Elwynn\r\n");
        fx.WriteStoreCsv("WorldSafeLocs", "ID,Name\r\n1,Stormwind\r\n");
        SeedServerDbc(fx, "AreaTable.dbc", "ID,Name\r\n1,Elwynn\r\n");
        SeedServerDbc(fx, "WorldSafeLocs.dbc", "ID,Name\r\n1,Stormwind\r\n");

        var package = fx.StackModule(DelvesInstallHook.CatalogId);
        WriteSql(package, Path.Combine("DBC_CSV", "DBFilesClient", "AreaTable.csv"), "ID,Name\r\n900,Stonetalon Ruins\r\n");
        WriteSql(package, Path.Combine("DBC_CSV", "DBFilesClient", "WorldSafelocs.csv"), "ID,Name\r\n900,Delve Entrance\r\n");
        Directory.CreateDirectory(Path.Combine(package, "MPQ", "Interface", "GlueXML"));
        File.WriteAllText(Path.Combine(package, "MPQ", "Interface", "GlueXML", "Delve.blp"), "art");
        Directory.CreateDirectory(Path.Combine(package, "Server Map Files", "maps"));
        Directory.CreateDirectory(Path.Combine(package, "Server Map Files", "mmaps"));
        Directory.CreateDirectory(Path.Combine(package, "Server Map Files", "vmaps"));
        File.WriteAllText(Path.Combine(package, "Server Map Files", "maps", "900.map"), "map");
        File.WriteAllText(Path.Combine(package, "Server Map Files", "mmaps", "900.mmtile"), "mmap");
        File.WriteAllText(Path.Combine(package, "Server Map Files", "vmaps", "900.vmtile"), "vmap");
        Directory.CreateDirectory(Path.Combine(package, "lua_scripts"));
        File.WriteAllText(Path.Combine(package, "lua_scripts", "delves.lua"), "-- delves\n");

        var orchestrator = fx.CreateOrchestrator(
            [DelvesInstallHook.CatalogId],
            new DelvesInstallHook());

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", DelvesInstallHook.CatalogId, "csv", "AreaTable.txt"))
            .Should().BeTrue();
        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", DelvesInstallHook.CatalogId, "csv", "WorldSafeLocs.txt"))
            .Should().BeTrue();
        fx.PublishedMpqBytes.Should().ContainKey(DelvesInstallHook.OverlayMpqFileName);
        using (var zip = new ZipArchive(new MemoryStream(fx.PublishedMpqBytes[DelvesInstallHook.OverlayMpqFileName])))
        {
            zip.Entries.Select(e => e.FullName.Replace('\\', '/'))
                .Should().Contain(name => name.Contains("Interface/GlueXML/Delve.blp", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("Delve.blp", StringComparison.OrdinalIgnoreCase));
        }

        File.ReadAllText(Path.Combine(fx.StackRoot, "lua_scripts", "delves.lua")).Should().Contain("delves");
        fx.PublishedDataVolume.Select(p => p.Subdir).Should().BeEquivalentTo(["maps", "mmaps", "vmaps"]);
        fx.PublishedDataVolume.Should().Contain(p => p.Subdir == "maps" && p.Files.Contains("900.map"));
        fx.PublishedDataVolume.Should().Contain(p => p.Subdir == "mmaps" && p.Files.Contains("900.mmtile"));
        fx.PublishedDataVolume.Should().Contain(p => p.Subdir == "vmaps" && p.Files.Contains("900.vmtile"));
        fx.PushedDbc.Should().Contain("AreaTable.dbc");
        fx.PushedDbc.Should().Contain("WorldSafeLocs.dbc");
    }

    [Fact]
    public async Task Module_without_a_hook_is_skipped_and_does_not_fail_apply()
    {
        using var fx = new Fixture();
        fx.StackModule("mod-plain-cpp");
        var orchestrator = fx.CreateOrchestrator(["mod-plain-cpp"]);
        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });
        fx.Wdbx.Exports.Should().BeEmpty();
        fx.RebuiltPatchD.Should().BeFalse();
    }

    [Fact]
    public async Task Apply_refuses_when_dbc_store_is_not_ready()
    {
        using var fx = new Fixture();
        fx.Store.Ready = false;
        var orchestrator = fx.CreateOrchestrator(
            ["mod-alpha"],
            new ArchiveDeltaHook("mod-alpha", "SkillLine"));
        var act = () => orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DBC baseline*");
    }

    [Fact]
    public async Task DescribeChoices_returns_groups_only_for_hooked_modules()
    {
        using var fx = new Fixture();
        var orchestrator = fx.CreateOrchestrator(
            [IndividualProgressionInstallHook.CatalogId, "mod-plain-cpp", PetBattleInstallHook.CatalogId],
            new IndividualProgressionInstallHook(),
            new PetBattleInstallHook());

        var dto = await orchestrator.DescribeChoicesAsync(fx.StackId);
        dto.Modules.Select(m => m.ModuleId).Should().BeEquivalentTo(
        [
            IndividualProgressionInstallHook.CatalogId
        ]);
    }

    [Fact]
    public async Task Overlay_mpq_with_dbc_and_art_is_stripped_then_published_without_dbc()
    {
        using var fx = new Fixture();
        var package = fx.StackModule("mod-client-pack");
        WriteZip(
            Path.Combine(package, "optional", "patch-X.mpq"),
            new Dictionary<string, string>
            {
                ["DBFilesClient/Spell.dbc"] = VanillaSpell,
                ["Interface/GLUES/login.blp"] = "art-bytes",
            });

        var orchestrator = fx.CreateOrchestrator(
            ["mod-client-pack"],
            new IncludeMpqHook("mod-client-pack", "optional/patch-X.mpq"));

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        fx.PublishedMpqBytes.Should().ContainKey("patch-X.mpq");
        using var zip = new ZipArchive(new MemoryStream(fx.PublishedMpqBytes["patch-X.mpq"]));
        var names = zip.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .ToList();
        names.Should().Contain(n => n.EndsWith("login.blp", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("DBFilesClient", StringComparison.OrdinalIgnoreCase));
        fx.RebuiltPatchD.Should().BeFalse();
    }

    [Fact]
    public async Task Dbc_only_overlay_mpq_is_not_published()
    {
        using var fx = new Fixture();
        var package = fx.StackModule("mod-dbc-pack");
        WriteZip(
            Path.Combine(package, "optional", "patch-Y.mpq"),
            new Dictionary<string, string>
            {
                ["Spell.dbc"] = VanillaSpell,
            });

        var orchestrator = fx.CreateOrchestrator(
            ["mod-dbc-pack"],
            new IncludeMpqHook("mod-dbc-pack", "optional/patch-Y.mpq"));

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        fx.PublishedMpqs.Should().BeEmpty();
        fx.PublishedMpqBytes.Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_mpq_strip_aborts_apply_and_does_not_publish_the_original()
    {
        using var fx = new Fixture();
        fx.Mpq.ThrowOnExtract = true;
        var package = fx.StackModule("mod-dbc-pack");
        WriteZip(
            Path.Combine(package, "optional", "patch-Y.mpq"),
            new Dictionary<string, string>
            {
                ["Spell.dbc"] = VanillaSpell,
                ["Interface/icon.blp"] = "keep-me",
            });

        var orchestrator = fx.CreateOrchestrator(
            ["mod-dbc-pack"],
            new IncludeMpqHook("mod-dbc-pack", "optional/patch-Y.mpq"));

        var act = () => orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*strip*");
        fx.PublishedMpqs.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_modules_cannot_both_set_Spell_as_the_import_base()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("Spell", VanillaSpell);
        SeedServerDbc(fx, "Spell.dbc", VanillaSpell);
        WriteDeltaModule(fx, "mod-alpha", "Spell.dbc", VanillaSpell);
        WriteDeltaModule(fx, "mod-beta", "Spell.dbc", VanillaSpell);

        var orchestrator = fx.CreateOrchestrator(
            ["mod-alpha", "mod-beta"],
            new SpellBaseHook("mod-alpha"),
            new SpellBaseHook("mod-beta"));

        var act = () => orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*mod-beta*Spell*mod-alpha*Spell*");
        fx.Wdbx.Imports.Should().BeEmpty();
        fx.RebuiltPatchD.Should().BeFalse();
    }

    [Fact]
    public async Task Apply_persists_module_data_when_the_stack_has_no_live_server_dbc_yet()
    {
        using var fx = new Fixture();
        fx.ServerDbcBaselineReady = false;
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        WriteDeltaModule(fx, "mod-alpha", "SkillLine.dbc", VanillaSkillLine + "90001,Alpha\r\n");

        var orchestrator = fx.CreateOrchestrator(
            ["mod-alpha"],
            new ArchiveDeltaHook("mod-alpha", "SkillLine"));

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", "mod-alpha", "selections.json")).Should().BeTrue();
        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", "mod-alpha", "csv", "SkillLine.txt")).Should().BeTrue();
        fx.Wdbx.Imports.Should().BeEmpty();
        fx.RebuiltPatchD.Should().BeFalse();
        fx.PushedDbc.Should().BeEmpty();
    }

    [Fact]
    public async Task Sql_files_are_applied_per_database_in_world_auth_characters_order()
    {
        using var fx = new Fixture();
        var package = fx.StackModule("mod-sql-pack");
        WriteSql(package, "data/sql/world/01_world.sql", "UPDATE item_template SET stackable = 20 WHERE entry = 1;");
        WriteSql(package, "data/sql/auth/02_auth.sql", "UPDATE account SET locked = 0 WHERE id = 1;");
        WriteSql(package, "data/sql/characters/03_characters.sql", "UPDATE characters SET money = 0 WHERE guid = 1;");

        var orchestrator = fx.CreateOrchestrator(
            ["mod-sql-pack"],
            new SqlFilesHook("mod-sql-pack",
            [
                "data/sql/world/01_world.sql",
                "data/sql/auth/02_auth.sql",
                "data/sql/characters/03_characters.sql"
            ]));

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        fx.SqlByDatabase.Keys.Should().BeEquivalentTo(["acore_world", "acore_auth", "acore_characters"]);
        fx.SqlApplyOrder.Should().Equal("acore_world", "acore_auth", "acore_characters");
        fx.SqlByDatabase["acore_world"].Should().Contain(p => p.EndsWith("01_world.sql"));
        fx.SqlByDatabase["acore_auth"].Should().Contain(p => p.EndsWith("02_auth.sql"));
        fx.SqlByDatabase["acore_characters"].Should().Contain(p => p.EndsWith("03_characters.sql"));
    }

    [Fact]
    public async Task ExtractArchive_strips_a_single_wrapper_folder()
    {
        using var fx = new Fixture();
        var package = fx.ModulePackage("mod-wrapped");
        WriteZip(
            Path.Combine(package, "optional", "dbc.7z"),
            new Dictionary<string, string>
            {
                ["dbc-export/SkillLine.dbc"] = VanillaSkillLine + "90001,Wrapped\r\n",
            });

        var (session, helpers) = fx.Helpers("mod-wrapped", package);
        using (session)
        {
            await helpers.ExtractArchive("optional/dbc.7z");
            await helpers.ExtractDbcByName("SkillLine");
            var csv = Path.Combine(session.ModuleDir("mod-wrapped"), "csv", "SkillLine.txt");
            (await File.ReadAllTextAsync(csv)).Should().Contain("90001,Wrapped");
        }
    }

    [Fact]
    public async Task IndividualProgression_loading_visual_publishes_patch_U()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        fx.WriteStoreCsv("SkillLineAbility", "ID,Name\r\n1,Vanilla\r\n");
        fx.WriteStoreCsv("SkillRaceClassInfo", "ID,Name\r\n1,Vanilla\r\n");
        fx.WriteStoreCsv("SpellItemEnchantment", "ID,Name\r\n1,Vanilla\r\n");
        SeedServerDbc(fx, "SkillLine.dbc", VanillaSkillLine);

        var package = fx.StackModule(IndividualProgressionInstallHook.CatalogId);
        WriteZip(
            Path.Combine(package, "optional", "dbc.7z"),
            new Dictionary<string, string>
            {
                ["SkillLine.dbc"] = VanillaSkillLine,
                ["SkillLineAbility.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
                ["SkillRaceClassInfo.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
                ["SpellItemEnchantment.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
            });
        WriteZip(
            Path.Combine(package, "optional", "patch-U.mpq"),
            new Dictionary<string, string>
            {
                ["Interface/GLUES/loading.blp"] = "loading-art",
            });

        var orchestrator = fx.CreateOrchestrator(
            [IndividualProgressionInstallHook.CatalogId],
            new IndividualProgressionInstallHook());

        await orchestrator.ApplyAsync(
            fx.StackId,
            new ApplyModuleExtraDataRequest
            {
                SelectionsByModuleId =
                {
                    [IndividualProgressionInstallHook.CatalogId] = new ModuleInstallSelections
                    {
                        Groups = { ["visuals"] = ["loading"] }
                    }
                }
            },
            _ => { });

        fx.PublishedMpqs.Should().Contain(p => p.Contains("patch-U", StringComparison.OrdinalIgnoreCase));
        fx.PublishedMpqs.Should().NotContain(p => p.Contains("patch-J", StringComparison.OrdinalIgnoreCase));
        fx.Wdbx.MpqExtracts.Should().NotContain(e => e.Filter == "Spell");
    }

    [Fact]
    public async Task Two_modules_coalesce_different_tables_independently()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        fx.WriteStoreCsv("Item", "ID,Name\r\n1,Sword\r\n");
        SeedServerDbc(fx, "SkillLine.dbc", VanillaSkillLine);
        SeedServerDbc(fx, "Item.dbc", "ID,Name\r\n1,Sword\r\n");

        WriteDeltaModule(fx, "mod-skills", "SkillLine.dbc", VanillaSkillLine + "90001,NewSkill\r\n");
        WriteDeltaModule(fx, "mod-items", "Item.dbc", "ID,Name\r\n1,Sword\r\n90010,NewItem\r\n");

        var orchestrator = fx.CreateOrchestrator(
            ["mod-skills", "mod-items"],
            new ArchiveDeltaHook("mod-skills", "SkillLine"),
            new ArchiveDeltaHook("mod-items", "Item"));

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });

        (await File.ReadAllTextAsync(Path.Combine(fx.StackRoot, "server_dbc", "SkillLine.dbc")))
            .Should().Contain("90001,NewSkill");
        (await File.ReadAllTextAsync(Path.Combine(fx.StackRoot, "server_dbc", "Item.dbc")))
            .Should().Contain("90010,NewItem");
        fx.PushedDbc.Should().BeEquivalentTo(["SkillLine.dbc", "Item.dbc"]);
        fx.RebuiltPatchD.Should().BeTrue();
    }

    [Fact]
    public async Task ServerWideProgression_mode_skips_IP_hook_and_still_prepares_other_modules()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        SeedServerDbc(fx, "SkillLine.dbc", VanillaSkillLine);

        var ip = fx.StackModule(IndividualProgressionInstallHook.CatalogId);
        WriteZip(
            Path.Combine(ip, "optional", "dbc.7z"),
            new Dictionary<string, string>
            {
                ["SkillLine.dbc"] = VanillaSkillLine + "90001,IPSkill\r\n",
            });
        WriteDeltaModule(fx, "mod-alpha", "SkillLine.dbc", VanillaSkillLine + "90002,Alpha\r\n");

        var orchestrator = fx.CreateOrchestrator(
            [IndividualProgressionInstallHook.CatalogId, "mod-alpha"],
            new IndividualProgressionInstallHook(),
            new ArchiveDeltaHook("mod-alpha", "SkillLine"));

        await orchestrator.PrepareAsync(
            fx.StackId,
            new ApplyModuleExtraDataRequest { IpContentMode = IpContentMode.ServerWideProgression },
            _ => { });

        Directory.Exists(Path.Combine(fx.StackRoot, "InstalledModules", IndividualProgressionInstallHook.CatalogId))
            .Should().BeFalse();
        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", "mod-alpha", "csv", "SkillLine.txt")).Should().BeTrue();
        fx.Wdbx.Imports.Should().BeEmpty();
        fx.PushedDbc.Should().BeEmpty();

        await orchestrator.DepositAsync(fx.StackId, _ => { });
        (await File.ReadAllTextAsync(Path.Combine(fx.StackRoot, "server_dbc", "SkillLine.dbc")))
            .Should().Contain("90002,Alpha")
            .And.NotContain("90001,IPSkill");
        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", "mod-alpha", "csv", "SkillLine.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Optional_sql_skips_deselected_files()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        fx.WriteStoreCsv("SkillLineAbility", "ID,Name\r\n1,Vanilla\r\n");
        fx.WriteStoreCsv("SkillRaceClassInfo", "ID,Name\r\n1,Vanilla\r\n");
        fx.WriteStoreCsv("SpellItemEnchantment", "ID,Name\r\n1,Vanilla\r\n");
        SeedServerDbc(fx, "SkillLine.dbc", VanillaSkillLine);

        var package = fx.StackModule(IndividualProgressionInstallHook.CatalogId);
        WriteZip(
            Path.Combine(package, "optional", "dbc.7z"),
            new Dictionary<string, string>
            {
                ["SkillLine.dbc"] = VanillaSkillLine,
                ["SkillLineAbility.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
                ["SkillRaceClassInfo.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
                ["SpellItemEnchantment.dbc"] = "ID,Name\r\n1,Vanilla\r\n",
            });
        Directory.CreateDirectory(Path.Combine(package, "optional", "sql", "world"));
        await File.WriteAllTextAsync(
            Path.Combine(package, "optional", "sql", "world", "zz_optional_ammo_stack_size.sql"),
            "UPDATE item_template SET stackable = 1000 WHERE entry = 2512;\n");
        await File.WriteAllTextAsync(
            Path.Combine(package, "optional", "sql", "world", "zz_optional_hardcore.sql"),
            "UPDATE player SET hardcore = 1;\n");

        var orchestrator = fx.CreateOrchestrator(
            [IndividualProgressionInstallHook.CatalogId],
            new IndividualProgressionInstallHook());

        await orchestrator.ApplyAsync(
            fx.StackId,
            new ApplyModuleExtraDataRequest
            {
                SelectionsByModuleId =
                {
                    [IndividualProgressionInstallHook.CatalogId] = new ModuleInstallSelections
                    {
                        Groups = { ["optional-sql"] = ["zz_optional_hardcore.sql"] }
                    }
                }
            },
            _ => { });

        fx.SqlByDatabase["acore_world"].Should().Contain(p => p.EndsWith("zz_optional_hardcore.sql"));
        fx.SqlByDatabase["acore_world"].Should().NotContain(p => p.EndsWith("zz_optional_ammo_stack_size.sql"));
    }

    [Fact]
    public async Task RemoveModuleExtras_deletes_the_folder_and_reapplies_remaining()
    {
        using var fx = new Fixture();
        fx.WriteStoreCsv("SkillLine", VanillaSkillLine);
        SeedServerDbc(fx, "SkillLine.dbc", VanillaSkillLine);
        WriteDeltaModule(fx, "mod-alpha", "SkillLine.dbc", VanillaSkillLine + "90001,Alpha\r\n");
        WriteDeltaModule(fx, "mod-beta", "SkillLine.dbc", VanillaSkillLine + "90002,Beta\r\n");

        var orchestrator = fx.CreateOrchestrator(
            ["mod-alpha", "mod-beta"],
            new ArchiveDeltaHook("mod-alpha", "SkillLine"),
            new ArchiveDeltaHook("mod-beta", "SkillLine"));

        await orchestrator.ApplyAsync(fx.StackId, new ApplyModuleExtraDataRequest(), _ => { });
        await orchestrator.RemoveModuleExtrasAsync(fx.StackId, "mod-alpha", _ => { });

        Directory.Exists(Path.Combine(fx.StackRoot, "InstalledModules", "mod-alpha")).Should().BeFalse();
        File.Exists(Path.Combine(fx.StackRoot, "InstalledModules", "mod-beta", "csv", "SkillLine.txt")).Should().BeTrue();
    }

    private static void WriteSql(string packageRoot, string relativePath, string sql)
    {
        var path = Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sql);
    }

    private static void WriteDeltaModule(Fixture fx, string moduleId, string dbcFile, string csv)
    {
        var package = fx.StackModule(moduleId);
        WriteZip(
            Path.Combine(package, "optional", "dbc.7z"),
            new Dictionary<string, string> { [dbcFile] = csv });
    }

    private static void SeedServerDbc(Fixture fx, string fileName, string csv)
    {
        var dir = Path.Combine(fx.StackRoot, "server_dbc");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), csv);
    }

    private static void WriteAddon(string packageRoot, string relativeDir, string marker)
    {
        var dir = Path.Combine(packageRoot, relativeDir);
        Directory.CreateDirectory(dir);
        var folder = Path.GetFileName(dir);
        File.WriteAllText(Path.Combine(dir, $"{folder}.toc"), $"## Title: {folder} {marker}\n");
        File.WriteAllText(Path.Combine(dir, "core.lua"), $"-- {marker}\n");
    }

    private static void WriteZip(string path, Dictionary<string, string> files) =>
        WriteZip(path, files.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value)));

    private static void WriteZip(string path, Dictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in files)
        {
            var entry = zip.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Fastest);
            using var dest = entry.Open();
            dest.Write(bytes);
        }
    }

    private static byte[] ZipBytes(Dictionary<string, string> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private sealed class ArchiveDeltaHook : IModuleInstallHook
    {
        private readonly string _table;

        public ArchiveDeltaHook(string moduleId, string table)
        {
            ModuleId = moduleId;
            _table = table;
        }

        public string ModuleId { get; }

        public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

        public async Task<ModuleInstallContribution> InstallAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default)
        {
            await context.Helpers.ExtractArchive("optional/dbc.7z", cancellationToken);
            await context.Helpers.ExtractDbcByName(_table, cancellationToken);
            return context.Helpers.Contribution;
        }
    }

    private sealed class IncludeMpqHook : IModuleInstallHook
    {
        private readonly string _relativeMpq;

        public IncludeMpqHook(string moduleId, string relativeMpq)
        {
            ModuleId = moduleId;
            _relativeMpq = relativeMpq;
        }

        public string ModuleId { get; }

        public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

        public async Task<ModuleInstallContribution> InstallAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default)
        {
            await context.Helpers.IncludeMpq(_relativeMpq, cancellationToken);
            return context.Helpers.Contribution;
        }
    }

    private sealed class SpellBaseHook : IModuleInstallHook
    {
        public SpellBaseHook(string moduleId) => ModuleId = moduleId;

        public string ModuleId { get; }

        public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

        public async Task<ModuleInstallContribution> InstallAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default)
        {
            await context.Helpers.ExtractArchive("optional/dbc.7z", cancellationToken);
            await context.Helpers.ExtractDbcByName("Spell", cancellationToken);
            context.Helpers.SetAsBaseDBC("Spell");
            return context.Helpers.Contribution;
        }
    }

    private sealed class SqlFilesHook : IModuleInstallHook
    {
        private readonly IReadOnlyList<string> _relativePaths;

        public SqlFilesHook(string moduleId, IReadOnlyList<string> relativePaths)
        {
            ModuleId = moduleId;
            _relativePaths = relativePaths;
        }

        public string ModuleId { get; }

        public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

        public async Task<ModuleInstallContribution> InstallAsync(
            ModuleInstallContext context, CancellationToken cancellationToken = default)
        {
            foreach (var relative in _relativePaths)
            {
                await context.Helpers.IncludeSql(relative, cancellationToken);
            }

            return context.Helpers.Contribution;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "azp-modinst-" + Guid.NewGuid().ToString("N"));
        public string StackId { get; } = "stack-modinst";
        public string BuildsPath => Path.Combine(Root, "builds");
        public string StackRoot => Path.Combine(BuildsPath, StackId);
        public string StoreDir => Path.Combine(Root, "dbc-store");
        public FakeWdbxCli Wdbx { get; } = new();
        public FakeMpqToolCli Mpq { get; } = new();
        public FileDbcStore Store { get; }
        public bool ServerDbcBaselineReady { get; set; } = true;
        public List<string> PushedDbc { get; } = [];
        public bool RebuiltPatchD { get; set; }
        public List<string> PublishedMpqs { get; } = [];
        public Dictionary<string, byte[]> PublishedMpqBytes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> SqlByDatabase { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SqlApplyOrder { get; } = [];
        public List<(string Source, string Folder)> InstalledAddons { get; } = [];
        public List<string> InstalledAddonTocs { get; } = [];
        public string? SavedWorldserverConf { get; set; }
        public List<(string Subdir, List<string> Files)> PublishedDataVolume { get; } = [];

        private AzerothCoreDbContext? _db;

        public Fixture()
        {
            Directory.CreateDirectory(BuildsPath);
            Directory.CreateDirectory(StackRoot);
            Directory.CreateDirectory(StoreDir);
            Store = new FileDbcStore(StoreDir);
        }

        public string ModulePackage(string moduleId)
        {
            var dir = Path.Combine(Root, "custom-modules", moduleId);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string StackModule(string moduleId)
        {
            var dir = Path.Combine(StackRoot, "azerothcore-wotlk", "modules", moduleId);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void WriteStoreCsv(string table, string csv) =>
            File.WriteAllText(Path.Combine(StoreDir, CsvNormalizer.TableFileName(table)), csv);

        public (ModuleInstallSession Session, ModuleInstallHelpers Helpers) Helpers(string moduleId, string packageRoot)
        {
            var session = new ModuleInstallSession(Path.Combine(Root, ".module-install", Guid.NewGuid().ToString("N")));
            var helpers = new ModuleInstallHelpers(moduleId, packageRoot, session, Wdbx, Store, Mpq);
            return (session, helpers);
        }

        public ModuleInstallOrchestrator CreateOrchestrator(IReadOnlyList<string> moduleIds, params IModuleInstallHook[] hooks)
        {
            _db?.Dispose();
            var options = new DbContextOptionsBuilder<AzerothCoreDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options;
            _db = new AzerothCoreDbContext(options);
            _db.Database.OpenConnection();
            _db.Database.EnsureCreated();
            _db.ManagedStacks.Add(new ManagedStackEntity
            {
                Id = StackId,
                StackName = StackId,
                NormalizedStackName = StackId,
                ModuleIdsJson = System.Text.Json.JsonSerializer.Serialize(moduleIds),
            });
            _db.SaveChanges();

            var migrations = new Mock<IMigrationService>();
            migrations
                .Setup(m => m.TryEnsureServerDbcBaselineAsync(StackId, It.IsAny<CancellationToken>()))
                .Returns((string _, CancellationToken _) => Task.FromResult(ServerDbcBaselineReady));
            migrations
                .Setup(m => m.PushServerDbcFilesAsync(StackId, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .Callback<string, IReadOnlyList<string>, CancellationToken>((_, names, _) => PushedDbc.AddRange(names))
                .Returns(Task.CompletedTask);
            migrations
                .Setup(m => m.RebuildPatchDAsync(StackId, It.IsAny<CancellationToken>()))
                .Callback(() => RebuiltPatchD = true)
                .Returns(Task.CompletedTask);
            migrations
                .Setup(m => m.PublishOverlayMpqAsync(StackId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((_, path, _) =>
                {
                    PublishedMpqs.Add(path);
                    if (File.Exists(path))
                    {
                        PublishedMpqBytes[Path.GetFileName(path)] = File.ReadAllBytes(path);
                    }
                })
                .Returns(Task.CompletedTask);
            migrations
                .Setup(m => m.ApplySqlFilesAsync(StackId, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, database, files, _) =>
                {
                    SqlApplyOrder.Add(database);
                    if (!SqlByDatabase.TryGetValue(database, out var list))
                    {
                        list = [];
                        SqlByDatabase[database] = list;
                    }

                    list.AddRange(files);
                })
                .Returns(Task.CompletedTask);
            migrations
                .Setup(m => m.PublishDataVolumeFilesAsync(
                    StackId, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, subdir, files, _) =>
                    PublishedDataVolume.Add((subdir, files.Select(Path.GetFileName).OfType<string>().ToList())))
                .Returns(Task.CompletedTask);

            var addons = new Mock<IAddonService>();
            addons
                .Setup(a => a.InstallFromDirectoryAsync(
                    StackId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string?, string, string, CancellationToken>((_, source, folder, _) =>
                {
                    InstalledAddons.Add((source, folder));
                    var toc = Directory.EnumerateFiles(source, "*.toc").FirstOrDefault();
                    if (toc is not null)
                    {
                        InstalledAddonTocs.Add(File.ReadAllText(toc));
                    }
                })
                .ReturnsAsync(new AddonListDto());

            var serverConfig = new Mock<IServerConfigService>();
            serverConfig
                .Setup(s => s.ReadAsync(StackId, "worldserver.conf", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ServerConfigContentDto
                {
                    Path = "worldserver.conf",
                    Content = "LogLevel = 1\n"
                });
            serverConfig
                .Setup(s => s.SaveAsync(StackId, "worldserver.conf", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string, CancellationToken>((_, _, content, _) => SavedWorldserverConf = content)
                .ReturnsAsync(new ServerConfigListDto { StackId = StackId });

            var packages = new Mock<IModulePackageStorage>();
            packages.Setup(p => p.HasPackage(It.IsAny<string>())).Returns(false);

            return new ModuleInstallOrchestrator(
                Store,
                new ModuleInstallHookRunner(hooks),
                Wdbx,
                Mpq,
                packages.Object,
                migrations.Object,
                addons.Object,
                serverConfig.Object,
                _db,
                Options.Create(new DockerOptions { BuildsPath = BuildsPath }),
                Options.Create(new MigrationOptions { DbcStoreAutoSyncOnStart = false }),
                NullLogger<ModuleInstallOrchestrator>.Instance);
        }

        public void Dispose()
        {
            _db?.Dispose();
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    private sealed class FileDbcStore : IDbcBaselineStore
    {
        public FileDbcStore(string dir) => Dir = dir;

        public string Dir { get; }
        public bool Ready { get; set; } = true;
        public string? StoreDirectory => Ready ? Dir : null;

        public DbcBaselineStoreDto GetStatus() => new()
        {
            Ready = Ready,
            TableCount = Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "*.txt").Count() : 0
        };

        public bool IsReady() => Ready;

        public string? FindTableCsv(string tableName)
        {
            if (!Ready || !Directory.Exists(Dir))
            {
                return null;
            }

            var expected = CsvNormalizer.TableFileName(tableName);
            return Directory.EnumerateFiles(Dir, "*.txt")
                .FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase));
        }

        public DbcBaselineStoreDto EnqueueSync(bool force = false) => GetStatus();

        public Task SyncAsync(bool force, Action<string>? onProgress, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Treats .dbc files as CSV text and .mpq files as zip archives of those files.</summary>
    private sealed class FakeWdbxCli : IWdbxCli
    {
        public List<string> Exports { get; } = [];
        public List<(string Mpq, string? Filter)> MpqExtracts { get; } = [];
        public List<(string Dbc, string Csv)> Imports { get; } = [];

        public async Task ExportDbcToCsvAsync(string dbcPath, string csvPath, CancellationToken cancellationToken = default)
        {
            Exports.Add(dbcPath);
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
            var text = await File.ReadAllTextAsync(dbcPath, cancellationToken);
            await CsvNormalizer.WriteCrlfAsync(csvPath, text, cancellationToken);
        }

        public Task ExtractDbcsFromMpqAsync(
            string mpqPath, string outputDir, string? filterName, CancellationToken cancellationToken = default)
        {
            MpqExtracts.Add((mpqPath, filterName));
            Directory.CreateDirectory(outputDir);
            var extracted = 0;
            using (var zip = ZipFile.OpenRead(mpqPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)
                        || !entry.Name.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (filterName is not null
                        && !string.Equals(
                            CsvNormalizer.NormalizeTableName(entry.Name),
                            CsvNormalizer.NormalizeTableName(filterName),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    entry.ExtractToFile(Path.Combine(outputDir, Path.GetFileName(entry.Name)), overwrite: true);
                    extracted++;
                }
            }

            if (extracted == 0)
            {
                throw new InvalidOperationException("No matching files found.");
            }

            return Task.CompletedTask;
        }

        public async Task ImportCsvAsync(string dbcPath, string csvPath, CancellationToken cancellationToken = default)
        {
            Imports.Add((dbcPath, csvPath));
            var start = File.Exists(dbcPath) ? await File.ReadAllTextAsync(dbcPath, cancellationToken) : string.Empty;
            var delta = await File.ReadAllTextAsync(csvPath, cancellationToken);
            await File.WriteAllTextAsync(dbcPath, MergeTakeNewest(start, delta), cancellationToken);
        }

        private static string MergeTakeNewest(string startCsv, string deltaCsv)
        {
            var startRows = Split(startCsv);
            var deltaRows = Split(deltaCsv);
            var header = startRows.Count > 0 ? startRows[0] : (deltaRows.Count > 0 ? deltaRows[0] : "ID");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in startRows.Skip(1))
            {
                var id = CsvNormalizer.FirstCsvField(line);
                if (id.Length > 0)
                {
                    map[id] = line;
                }
            }

            foreach (var line in deltaRows.Skip(1))
            {
                var id = CsvNormalizer.FirstCsvField(line);
                if (id.Length > 0)
                {
                    map[id] = line;
                }
            }

            var body = new List<string> { header };
            body.AddRange(map.Values);
            return CsvNormalizer.EnsureTrailingCrlf(string.Join("\r\n", body));
        }

        private static List<string> Split(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd())
                .Where(line => line.Length > 0)
                .ToList();
    }

    private sealed class FakeMpqToolCli : IMpqToolCli
    {
        public bool ThrowOnExtract { get; set; }

        public Task ExtractAllAsync(string mpqPath, string outputDir, CancellationToken cancellationToken)
        {
            if (ThrowOnExtract)
            {
                throw new InvalidOperationException("mpqtool extract failed.");
            }

            Directory.CreateDirectory(outputDir);
            ZipFile.ExtractToDirectory(mpqPath, outputDir, overwriteFiles: true);
            return Task.CompletedTask;
        }

        public Task PackPreservePathsAsync(string sourceDir, string outputMpq, CancellationToken cancellationToken)
        {
            if (File.Exists(outputMpq))
            {
                File.Delete(outputMpq);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputMpq)!);
            ZipFile.CreateFromDirectory(sourceDir, outputMpq, CompressionLevel.Fastest, includeBaseDirectory: false);
            return Task.CompletedTask;
        }
    }
}
