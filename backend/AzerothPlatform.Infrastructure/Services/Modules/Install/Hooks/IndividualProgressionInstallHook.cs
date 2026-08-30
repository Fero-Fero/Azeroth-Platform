using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// Extra-data recipe for Grimfeather/mod-individual-progression. Does not implement Server Wide Progression.
/// </summary>
public sealed class IndividualProgressionInstallHook : IModuleInstallHook
{
    public const string CatalogId = "mod-individual-progression";

    public static readonly string[] DeltaDbcTables =
    [
        "SkillLine",
        "SkillLineAbility",
        "SkillRaceClassInfo",
        "SpellItemEnchantment"
    ];

    /// <summary>
    /// Named optional world SQL files shipped under <c>optional/sql/world/</c>. Missing files are skipped
    /// so a renamed upstream file does not fail the whole extra-data apply.
    /// </summary>
    public static readonly string[] OptionalWorldSql =
    [
        "optional/sql/world/zz_optional_ammo_stack_size.sql",
        "optional/sql/world/zz_optional_aq_war_effort.sql",
        "optional/sql/world/zz_optional_hardcore.sql",
        "optional/sql/world/zz_optional_hardcore_mode.sql",
        "optional/sql/world/zz_optional_instant_flight.sql",
        "optional/sql/world/zz_optional_item_upgrade.sql",
        "optional/sql/world/zz_optional_racial_mounts.sql",
        "optional/sql/world/zz_optional_random_bot_level.sql",
        "optional/sql/world/zz_optional_starting_gold.sql",
        "optional/sql/world/zz_optional_starting_money.sql",
        "optional/sql/world/zz_optional_tbc_flying.sql",
        "optional/sql/world/zz_optional_vanilla_questgiver_greetings.sql",
        "optional/sql/world/zz_optional_vanilla_tbc_xp_rates.sql",
        "optional/sql/world/zz_optional_xp_rates.sql"
    ];

    public string ModuleId => CatalogId;

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModuleInstallChoiceGroup> groups =
        [
            new()
            {
                Id = "mana-costs",
                Title = "Spell mana costs",
                Description =
                    "Vanilla / TBC mana costs replace Spell.dbc as the import base. " +
                    "WotLK mana keeps patch-S Spell.dbc as the base. Do not use both.",
                Kind = ModuleInstallChoiceKind.Exclusive,
                AllowNone = false,
                Choices =
                [
                    new()
                    {
                        Id = "patch-s",
                        Label = "Keep WotLK mana costs during Vanilla and TBC",
                        Description = "Place patch-S.MPQ on the client and Spell.dbc from patch-S.7z on the server.",
                        DefaultSelected = true
                    },
                    new()
                    {
                        Id = "patch-v",
                        Label = "Vanilla / TBC mana costs",
                        Description = "Place patch-V.MPQ on the client and Spell.dbc from patch-V.7z on the server."
                    }
                ]
            },
            new()
            {
                Id = "visuals",
                Title = "Client visuals",
                Description = "Vanilla login and loading-screen MPQs. Unchecked keeps WotLK art.",
                Kind = ModuleInstallChoiceKind.Independent,
                Choices =
                [
                    new()
                    {
                        Id = "login",
                        Label = "Vanilla / TBC login screen (patch-J)",
                        Description = "Place patch-J.MPQ in the client Data folder. Visual only; no server change."
                    },
                    new()
                    {
                        Id = "loading",
                        Label = "Vanilla / TBC loading screens (patch-U)",
                        Description = "Place patch-U.MPQ in the client Data folder. Visual only; no server change."
                    }
                ]
            },
            new()
            {
                Id = "optional-sql",
                Title = "Optional SQL",
                Description = "Optional world SQL shipped under optional/sql/world. Uncheck files you do not want.",
                Kind = ModuleInstallChoiceKind.Independent,
                Choices = OptionalWorldSql.Select(relative =>
                {
                    var file = Path.GetFileName(relative);
                    return new ModuleInstallChoice
                    {
                        Id = file,
                        Label = HumanizeSqlFile(file),
                        DefaultSelected = true
                    };
                }).ToList()
            }
        ];
        return Task.FromResult(groups);
    }

    public async Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default)
    {
        var helpers = context.Helpers;
        await helpers.ExtractArchive("optional/dbc.7z", cancellationToken);
        foreach (var table in DeltaDbcTables)
        {
            await helpers.ExtractDbcByName(table, cancellationToken);
        }

        var sqlGroupPresent = context.Selections.Groups.ContainsKey("optional-sql");
        foreach (var relative in OptionalWorldSql)
        {
            var file = Path.GetFileName(relative);
            if (sqlGroupPresent && !context.Selections.IndependentContains("optional-sql", file))
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(
                context.PackageRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(full))
            {
                await helpers.IncludeSql(relative, cancellationToken);
            }
        }

        helpers.AddConfHint("PlayerSettings.EnablePlayerSettings", "1");
        helpers.AddConfHint("DBC.EnforceItemAttributes", "0");

        var mana = context.Selections.Exclusive("mana-costs") ?? "patch-s";
        if (string.Equals(mana, "patch-v", StringComparison.OrdinalIgnoreCase)
            && PackageFileExists(context, "optional/patch-V.7z"))
        {
            await ApplySpellBaseAsync(helpers, "optional/patch-V.7z", "patch-V.mpq", cancellationToken);
        }
        else if (string.Equals(mana, "patch-s", StringComparison.OrdinalIgnoreCase)
                 && PackageFileExists(context, "optional/patch-S.7z"))
        {
            await ApplySpellBaseAsync(helpers, "optional/patch-S.7z", "patch-S.mpq", cancellationToken);
        }

        if (context.Selections.IndependentContains("visuals", "login"))
        {
            await helpers.IncludeMpq("optional/patch-J.mpq", cancellationToken);
        }

        if (context.Selections.IndependentContains("visuals", "loading"))
        {
            await helpers.IncludeMpq("optional/patch-U.mpq", cancellationToken);
        }

        return helpers.Contribution;
    }

    private static bool PackageFileExists(ModuleInstallContext context, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(
            context.PackageRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(full);
    }

    private static async Task ApplySpellBaseAsync(
        IModuleInstallHelpers helpers, string archive, string mpqName, CancellationToken cancellationToken)
    {
        await helpers.ExtractArchive(archive, cancellationToken);
        await helpers.ExtractDbcsFromMpq(mpqName, "Spell", cancellationToken);
        helpers.SetAsBaseDBC("Spell");
        await helpers.IncludeMpq(mpqName, cancellationToken);
    }

    internal static string HumanizeSqlFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.StartsWith("zz_optional_", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem["zz_optional_".Length..];
        }

        stem = stem.Replace('_', ' ');
        return stem.Length == 0 ? fileName : char.ToUpperInvariant(stem[0]) + stem[1..];
    }
}
