using AzerothPlatform.Infrastructure.Services.Modules.Install;
using AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class PlayerbotDungeonSimInstallHookTests
{
    [Fact]
    public void RewriteSql_leaves_plain_updates_alone()
    {
        const string sql = "ALTER TABLE `characters` ADD COLUMN `foo` INT NOT NULL DEFAULT 0;\n";
        Assert.Equal(sql, PlayerbotDungeonSimInstallHook.RewriteSql(sql));
    }

    [Fact]
    public void RewriteSql_replaces_delimiter_procedure_with_select()
    {
        const string sql = """
            DROP PROCEDURE IF EXISTS `playerbot_dungeon_sim_add_column`;
            DELIMITER $$
            CREATE PROCEDURE `playerbot_dungeon_sim_add_column`(
                IN p_table VARCHAR(64)
            )
            BEGIN
                SELECT 1;
            END$$
            DELIMITER ;
            CALL `playerbot_dungeon_sim_add_column`('playerbot_dungeon_run');
            DROP PROCEDURE IF EXISTS `playerbot_dungeon_sim_add_column`;
            """;

        Assert.Equal(PlayerbotDungeonSimInstallHook.SilentStub, PlayerbotDungeonSimInstallHook.RewriteSql(sql));
    }

    [Fact]
    public void RewriteSql_replaces_poisoned_comment_select_with_select_only()
    {
        const string sql =
            "-- AzerothCore db-import cannot execute mysql-client procedure scripts.\n" +
            "-- Schema for this file is created by base SQL (CREATE TABLE IF NOT EXISTS).\n" +
            "SELECT 1;\n";

        Assert.Equal(PlayerbotDungeonSimInstallHook.SilentStub, PlayerbotDungeonSimInstallHook.RewriteSql(sql));
    }

    [Fact]
    public void RewriteSql_keeps_legacy_select_stub()
    {
        Assert.Equal(
            PlayerbotDungeonSimInstallHook.LegacySelectStub,
            PlayerbotDungeonSimInstallHook.RewriteSql("SELECT 1;\n"));
    }

    [Fact]
    public void RewriteSql_replaces_truncated_create_table_select_stub()
    {
        const string sql = "CREATE TABLE IF NOT EXISTS).\r\n\r\nSELECT 1;\r\n";
        Assert.Equal(PlayerbotDungeonSimInstallHook.SilentStub, PlayerbotDungeonSimInstallHook.RewriteSql(sql));
    }

    [Fact]
    public void RewriteSql_strips_ashbringer_use_and_keeps_commands()
    {
        const string sql = """
            USE azc_world_ashbringer;

            DELETE FROM `command` WHERE `name` IN (
             'dng-sim',
             'dng-sim help'
            );

            REPLACE INTO `command` (`name`, `security`, `help`) VALUES
            ('dng-sim', 3, 'Syntax: .dng-sim help');
            """;

        var rewritten = PlayerbotDungeonSimInstallHook.RewriteSql(sql);
        Assert.DoesNotContain("USE ", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("azc_world_ashbringer", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DELETE FROM `command`", rewritten, StringComparison.Ordinal);
        Assert.Contains("REPLACE INTO `command`", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteSql_keeps_create_table_from_force_install()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS `playerbot_dungeon_run` (
              `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            DROP PROCEDURE IF EXISTS `playerbot_dungeon_sim_add_column`;
            DELIMITER $$
            CREATE PROCEDURE `playerbot_dungeon_sim_add_column`()
            BEGIN
              SELECT 1;
            END$$
            DELIMITER ;
            """;

        var rewritten = PlayerbotDungeonSimInstallHook.RewriteSql(sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS `playerbot_dungeon_run`", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("DELIMITER $$", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE PROCEDURE", rewritten, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RewriteSql_noops_live_run_alters_that_run_before_create_table()
    {
        const string sql = """
            DROP PROCEDURE IF EXISTS `playerbot_dungeon_sim_add_column`;
            DELIMITER $$
            CREATE PROCEDURE `playerbot_dungeon_sim_add_column`(
             IN p_table VARCHAR(64),
             IN p_column VARCHAR(64),
             IN p_definition TEXT
            )
            BEGIN
             SET @s = CONCAT('ALTER TABLE `', p_table, '` ADD COLUMN `', p_column, '` ', p_definition);
             PREPARE stmt FROM @s;
             EXECUTE stmt;
             DEALLOCATE PREPARE stmt;
            END$$
            DELIMITER ;

            CALL `playerbot_dungeon_sim_add_column`('playerbot_dungeon_run', 'real_group_created', 'TINYINT UNSIGNED NOT NULL DEFAULT 0 AFTER `bosses_killed`');
            DROP PROCEDURE IF EXISTS `playerbot_dungeon_sim_add_column`;
            """;

        Assert.Equal(PlayerbotDungeonSimInstallHook.SilentStub, PlayerbotDungeonSimInstallHook.RewriteSql(sql));
    }

    [Fact]
    public void RewriteSql_keeps_force_install_create_tables()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS `playerbot_dungeon_run` (
              `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
              `bosses_killed` TINYINT UNSIGNED NOT NULL DEFAULT 0,
              `real_group_created` TINYINT UNSIGNED NOT NULL DEFAULT 0,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS `playerbot_dungeon_run_member` (
              `run_id` BIGINT UNSIGNED NOT NULL,
              `guid` INT UNSIGNED NOT NULL,
              PRIMARY KEY (`run_id`,`guid`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            DROP PROCEDURE IF EXISTS `playerbot_dungeon_sim_add_column`;
            DELIMITER $$
            CREATE PROCEDURE `playerbot_dungeon_sim_add_column`()
            BEGIN
              SELECT 1;
            END$$
            DELIMITER ;

            CALL `playerbot_dungeon_sim_add_column`('playerbot_dungeon_run', 'real_group_created', 'TINYINT UNSIGNED NOT NULL DEFAULT 0 AFTER `bosses_killed`');
            """;

        var rewritten = PlayerbotDungeonSimInstallHook.RewriteSql(sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS `playerbot_dungeon_run`", rewritten, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS `playerbot_dungeon_run_member`", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("DELIMITER", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CALL", rewritten, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareCheckout_rewrites_this_module_sql_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-dungeon-sim-" + Guid.NewGuid().ToString("N"));
        var simSql = Path.Combine(root, "mod-playerbot-dungeon-sim", "data", "sql", "updates", "2026_06_24_01.sql");
        var otherSql = Path.Combine(root, "mod-playerbots", "data", "sql", "plain.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(simSql)!);
        Directory.CreateDirectory(Path.GetDirectoryName(otherSql)!);
        File.WriteAllText(simSql, "DELIMITER $$\nCREATE PROCEDURE `x`() BEGIN SELECT 1; END$$\nDELIMITER ;\n");
        File.WriteAllText(otherSql, "SELECT 2;\n");

        try
        {
            var hook = new PlayerbotDungeonSimInstallHook();
            hook.PrepareCheckout(Path.Combine(root, "mod-playerbot-dungeon-sim")).Should().ContainSingle();
            File.ReadAllText(simSql).Should().Be(PlayerbotDungeonSimInstallHook.SilentStub);
            File.ReadAllText(otherSql).Should().Be("SELECT 2;\n");

            var runner = new ModuleInstallHookRunner([hook]);
            runner.PrepareCheckouts(root).Should().ContainSingle();
            File.ReadAllText(simSql).Should().Be(PlayerbotDungeonSimInstallHook.SilentStub);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrepareCheckout_stubs_updates_that_sort_before_force_install()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-dungeon-sim-" + Guid.NewGuid().ToString("N"));
        var updates = Path.Combine(root, "data", "sql", "db-world", "updates");
        Directory.CreateDirectory(updates);
        var early = Path.Combine(updates, "2026_06_24_06_playerbot_dungeon_sim_vanilla_raid_ladder.sql");
        var force = Path.Combine(updates, "2026_06_24_99_playerbot_dungeon_sim_force_world_install.sql");
        var later = Path.Combine(updates, "2026_06_25_01_playerbot_dungeon_sim_commands.sql");
        File.WriteAllText(early, "INSERT IGNORE INTO `playerbot_dungeon_template` (`id`) VALUES (101);\n");
        File.WriteAllText(
            force,
            "CREATE TABLE IF NOT EXISTS `playerbot_dungeon_template` (`id` INT PRIMARY KEY);\nINSERT INTO `playerbot_dungeon_template` (`id`) VALUES (101);\n");
        File.WriteAllText(later, "USE azc_world_ashbringer;\nINSERT INTO `command` (`name`) VALUES ('dngsim');\n");

        try
        {
            var hook = new PlayerbotDungeonSimInstallHook();
            hook.PrepareCheckout(root).Should().Contain(early);
            File.ReadAllText(early).Should().Be(PlayerbotDungeonSimInstallHook.SilentStub);
            File.ReadAllText(force).Should().Contain("CREATE TABLE IF NOT EXISTS `playerbot_dungeon_template`");
            File.ReadAllText(later).Should().Contain("INSERT INTO `command`");
            File.ReadAllText(later).Should().NotContain("azc_world_ashbringer");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrepareCheckout_leaves_already_applied_select_stub()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-dungeon-sim-" + Guid.NewGuid().ToString("N"));
        var updates = Path.Combine(root, "data", "sql", "db-world", "updates");
        Directory.CreateDirectory(updates);
        var early = Path.Combine(updates, "2026_06_24_06_playerbot_dungeon_sim_vanilla_raid_ladder.sql");
        var force = Path.Combine(updates, "2026_06_24_99_playerbot_dungeon_sim_force_world_install.sql");
        File.WriteAllText(early, PlayerbotDungeonSimInstallHook.LegacySelectStub);
        File.WriteAllText(force, "CREATE TABLE IF NOT EXISTS `playerbot_dungeon_template` (`id` INT PRIMARY KEY);\n");

        try
        {
            new PlayerbotDungeonSimInstallHook().PrepareCheckout(root);
            File.ReadAllText(early).Should().Be(PlayerbotDungeonSimInstallHook.LegacySelectStub);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
