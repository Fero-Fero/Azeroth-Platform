using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.Stacks;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Stacks;

public sealed class ModuleCheckCompilerTests
{
    [Fact]
    public void AttributeErrorsToModules_maps_clang_errors_by_module_folder()
    {
        const string log = """
            [1/4] Building CXX object modules/CMakeFiles/modules.dir/mod-playerbots/src/Bot.cpp.o
            [2/4] Building CXX object modules/CMakeFiles/modules.dir/mod-dungeon-clear/src/Foo.cpp.o
            /data/stacks/s1/azerothcore-wotlk/modules/mod-dungeon-clear/src/Foo.cpp:12:5: error: no member named 'Bar' in 'Player'
            [3/4] Building CXX object modules/CMakeFiles/modules.dir/mod-optimal-bot-raid/src/Raid.cpp.o
            /data/stacks/s1/azerothcore-wotlk/modules/mod-optimal-bot-raid/src/Raid.cpp:40:1: error: unknown type name 'MissingType'
            """;

        var attributed = ModuleCheckCompiler.AttributeErrorsToModules(
            log,
            ["mod-playerbots", "mod-dungeon-clear", "mod-optimal-bot-raid"]);

        attributed.Should().ContainKey("mod-dungeon-clear");
        attributed["mod-dungeon-clear"].Should().Contain("Foo.cpp");
        attributed.Should().ContainKey("mod-optimal-bot-raid");
        attributed["mod-optimal-bot-raid"].Should().Contain("Raid.cpp");
        attributed.Should().NotContainKey("mod-playerbots");
    }

    [Fact]
    public void ParseNinjaTargets_reads_top_level_names()
    {
        const string text = """
            all: phony
            modules: STATIC_LIBRARY
            worldserver: EXECUTABLE
            modules/CMakeFiles/modules.dir/src/Foo.cpp.o: CXX_COMPILER
            """;

        var names = ModuleCheckCompiler.ParseNinjaTargets(text);
        names.Should().Contain("modules");
        names.Should().Contain("worldserver");
        names.Should().NotContain("modules/CMakeFiles/modules.dir/src/Foo.cpp.o");
    }

    [Fact]
    public void ApplyCompileLine_marks_compiling_from_ninja_object_path()
    {
        var items = new[]
        {
            new ModuleCheckItemDto { ModuleId = "mod-dungeon-clear", Name = "Dungeon Clear", Status = "pending" },
            new ModuleCheckItemDto { ModuleId = "mod-playerbots", Name = "Playerbots", Status = "pending" },
        };

        ModuleCheckCompiler.ApplyCompileLine(
            "[12/400] Building CXX object modules/CMakeFiles/modules.dir/mod-dungeon-clear/src/Foo.cpp.o",
            items).Should().BeTrue();

        items[0].Status.Should().Be("compiling");
        items[1].Status.Should().Be("pending");
    }

    [Fact]
    public void ApplyCompileLine_marks_failed_from_clang_error()
    {
        var items = new[]
        {
            new ModuleCheckItemDto { ModuleId = "mod-dungeon-clear", Name = "Dungeon Clear", Status = "compiling" },
        };

        ModuleCheckCompiler.ApplyCompileLine(
            "/data/stacks/s1/azerothcore-wotlk/modules/mod-dungeon-clear/src/Foo.cpp:12:5: error: no member named 'Bar'",
            items).Should().BeTrue();

        items[0].Status.Should().Be("failed");
        items[0].Error.Should().Contain("Foo.cpp");
    }

    [Fact]
    public void DrainCompileLines_splits_carriage_return_progress()
    {
        var pending = new System.Text.StringBuilder("[1/4] Building CXX object a.o\r[2/4] Building CXX object b.o\n");
        var lines = ModuleCheckCompiler.DrainCompileLines(pending, flush: false);
        lines.Should().Equal("[1/4] Building CXX object a.o", "[2/4] Building CXX object b.o");
        pending.ToString().Should().BeEmpty();
    }

    [Fact]
    public void TryParseNinjaProgress_reads_counters()
    {
        ModuleCheckCompiler.TryParseNinjaProgress("[12/400] Building CXX object foo", out var current, out var total)
            .Should().BeTrue();
        current.Should().Be(12);
        total.Should().Be(400);
    }

    [Fact]
    public void AttributeErrorsToModules_ignores_warnings()
    {
        const string log = """
            /src/modules/mod-dungeon-clear/src/Foo.cpp:12:5: warning: unused variable 'x'
            """;

        ModuleCheckCompiler.AttributeErrorsToModules(log, ["mod-dungeon-clear"])
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void LooksLikeSuccessfulModulesLink_ignores_docker_wait_eof()
    {
        const string log = """
            [876/877] Building CXX object modules/CMakeFiles/modules.dir/mod-playerbots/src/Mgr/Travel/TravelNode.cpp.o
            time="2026-08-20T15:09:04Z" level=error msg="error waiting for container: unexpected EOF"
            [877/877] Linking CXX static library modules/libmodules.a
            """;

        ModuleCheckCompiler.LooksLikeSuccessfulModulesLink(log).Should().BeTrue();
        ModuleCheckCompiler.LooksLikeDockerWaitFailure(log).Should().BeTrue();
        ModuleCheckCompiler.AttributeErrorsToModules(log, ["mod-playerbots"]).Should().BeEmpty();
        ModuleCheckCompiler.TrimError(log, "fallback").Should().Be("fallback");
    }

    [Fact]
    public void LooksLikeSuccessfulWorldserverLink_true_when_executable_linked()
    {
        const string log = """
            [1200/1200] Linking CXX executable src/server/apps/worldserver
            """;

        ModuleCheckCompiler.LooksLikeSuccessfulWorldserverLink(log).Should().BeTrue();
    }

    [Fact]
    public void LooksLikeSuccessfulWorldserverLink_false_on_undefined_symbol()
    {
        const string log = """
            ld.lld: error: undefined symbol: DungeonClearControl::StartAutonomousClear(Player*)
            >>> referenced by mod_playerbot_dungeon_sim.cpp:1237
            >>>               modules/CMakeFiles/modules.dir/mod-playerbot-dungeon-sim/src/mod_playerbot_dungeon_sim.cpp.o:(...)
            clang++: error: linker command failed with exit code 1
            ninja: build stopped: subcommand failed.
            """;

        ModuleCheckCompiler.LooksLikeSuccessfulWorldserverLink(log).Should().BeFalse();
        var attributed = ModuleCheckCompiler.AttributeErrorsToModules(
            log,
            ["mod-dungeon-clear", "mod-playerbot-dungeon-sim"]);
        attributed.Should().ContainKey("mod-playerbot-dungeon-sim");
        attributed["mod-playerbot-dungeon-sim"].Should().Contain("StartAutonomousClear");
        attributed.Should().NotContainKey("mod-dungeon-clear");
    }

    [Fact]
    public void AttributeErrorsToModules_maps_gnu_ld_undefined_reference()
    {
        const string log = """
            /usr/bin/ld: modules/libmodules.a(mod_playerbot_dungeon_sim.cpp.o): in function `TryStartClear':
            /data/stacks/s1/azerothcore-wotlk/modules/mod-playerbot-dungeon-sim/src/mod_playerbot_dungeon_sim.cpp:1237: undefined reference to `DungeonClearControl::StartAutonomousClear(Player*)'
            """;

        var attributed = ModuleCheckCompiler.AttributeErrorsToModules(
            log,
            ["mod-playerbot-dungeon-sim", "mod-dungeon-clear"]);
        attributed.Should().ContainKey("mod-playerbot-dungeon-sim");
        attributed["mod-playerbot-dungeon-sim"].Should().Contain("undefined reference");
    }

    [Fact]
    public void LineMentionsModule_matches_checkout_folder_alias()
    {
        const string line =
            "[12/400] Building CXX object modules/CMakeFiles/modules.dir/mod-native-folder/src/mod.cpp.o";

        var items = new[]
        {
            new ModuleCheckItemDto
            {
                ModuleId = "mod-catalog-id",
                Name = "Aliased",
                Status = "pending",
                CheckoutFolder = "mod-native-folder",
            },
        };
        ModuleCheckCompiler.LineMentionsModule(line, "mod-catalog-id", "mod-native-folder")
            .Should()
            .BeTrue();
        ModuleCheckCompiler.LineMentionsModule(line, "mod-playerbots").Should().BeFalse();
        ModuleCheckCompiler.ApplyCompileLine(line, items).Should().BeTrue();
        items[0].Status.Should().Be("compiling");
    }

    [Fact]
    public void ContainerName_is_docker_safe_and_prefixed()
    {
        ModuleCheckCompiler.ContainerName("abc-123").Should().Be("azp-modcheck-abc-123");
        ModuleCheckCompiler.ContainerName("weird name!").Should().Be("azp-modcheck-weirdname");
    }

    [Fact]
    public void DeleteBuildDirectory_removes_cmake_tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-modcheck-test-" + Guid.NewGuid().ToString("N"));
        var buildDir = ModuleCheckCompiler.BuildDirectory(root);
        Directory.CreateDirectory(Path.Combine(buildDir, "CMakeFiles"));
        File.WriteAllText(Path.Combine(buildDir, "build.ninja"), "x");

        try
        {
            ModuleCheckCompiler.DeleteBuildDirectory(root);
            Directory.Exists(buildDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
