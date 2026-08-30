using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class ModuleCompileEnvironmentTests
{
    private static readonly string[] OllamaApt = ["libcurl4-openssl-dev", "nlohmann-json3-dev"];

    private static ModuleDto Module(string id, ModuleCompileProfile? compile = null) => new()
    {
        Id = id,
        Name = id,
        Compile = compile ?? ModuleCompileProfile.Empty,
    };

    private static readonly ModuleDto Playerbots = Module("mod-playerbots");

    private static readonly ModuleDto OllamaBuddy = Module(
        "mod-ollama-bot-buddy",
        new ModuleCompileProfile
        {
            ExtraAptPackages = OllamaApt,
        });

    private static readonly ModuleDto Aliased = Module(
        "mod-aliased",
        new ModuleCompileProfile
        {
            CheckoutFolder = "mod-native-folder",
            ExtraAptPackages = OllamaApt,
            ConflictsWith = ["mod-incompatible"],
            Companions =
            [
                new CompileCompanionModule
                {
                    Id = "mod-companion",
                    Name = "Compile companion",
                    Repository = "https://github.com/example/mod-companion",
                    Branch = "main",
                },
            ],
        });

    private static readonly ModuleDto Incompatible = Module(
        "mod-incompatible",
        new ModuleCompileProfile
        {
            ConflictsWith = ["mod-aliased"],
        });

    private static readonly ModuleDto DungeonClear = Module("mod-dungeon-clear");

    private static readonly ModuleDto DungeonSim = Module(
        "mod-playerbot-dungeon-sim",
        new ModuleCompileProfile
        {
            BranchPins =
            [
                new ModuleBranchPin { ModuleId = "mod-dungeon-clear", Branch = "auto-playerbots" },
            ],
        });

    [Fact]
    public void ExtraAptPackagesFor_unions_packages_from_selected_profiles()
    {
        ModuleCompileEnvironment.ExtraAptPackagesFor([Playerbots]).Should().BeEmpty();
        ModuleCompileEnvironment.ExtraAptPackagesFor([OllamaBuddy])
            .Should()
            .Equal(OllamaApt);
        ModuleCompileEnvironment.ExtraAptPackagesFor([Aliased])
            .Should()
            .Equal(OllamaApt);
    }

    [Fact]
    public void RequiredBranchFor_applies_branch_pins_from_selected_modules()
    {
        ModuleCompileEnvironment.RequiredBranchFor("mod-dungeon-clear", [DungeonClear])
            .Should()
            .BeNull();
        ModuleCompileEnvironment.RequiredBranchFor("mod-dungeon-clear", [DungeonClear, DungeonSim])
            .Should()
            .Be("auto-playerbots");
        ModuleCompileEnvironment.RequiredBranchFor("mod-playerbots", [Playerbots, DungeonSim])
            .Should()
            .BeNull();
    }

    [Fact]
    public void FixCaseMismatchedIncludes_rewrites_quoted_header_to_on_disk_casing()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-include-case-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "Dungeonclearautostart.h"), "struct DungeonClearControl {};");
        File.WriteAllText(Path.Combine(src, "Player.h"), "// not used — core headers must not be rewritten");
        var cpp = Path.Combine(src, "Ai", "DungeonClearChatActions.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(cpp)!);
        File.WriteAllText(cpp, """
            #include "DungeonClearAutoStart.h"
            #include "Player.h"
            bool DungeonClearControl::StartAutonomousClear(Player*) { return true; }
            """);

        try
        {
            var message = ModuleCompileEnvironment.FixCaseMismatchedIncludes(root);
            message.Should().Contain("Dungeonclearautostart.h");
            var text = File.ReadAllText(cpp);
            text.Should().Contain("#include \"Dungeonclearautostart.h\"");
            text.Should().NotContain("#include \"DungeonClearAutoStart.h\"");
            text.Should().Contain("#include \"Player.h\"");

            ModuleCompileEnvironment.FixCaseMismatchedIncludes(root).Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FixCaseMismatchedIncludes_ignores_headers_that_are_not_in_the_module()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-include-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cpp = Path.Combine(root, "mod.cpp");
        File.WriteAllText(cpp, "#include \"ScriptMgr.h\"\n#include \"Player.h\"\n");

        try
        {
            ModuleCompileEnvironment.FixCaseMismatchedIncludes(root).Should().BeNull();
            File.ReadAllText(cpp).Should().Contain("#include \"ScriptMgr.h\"");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompanionsFor_returns_companions_declared_on_selected_profiles()
    {
        ModuleCompileEnvironment.CompanionsFor([OllamaBuddy]).Should().BeEmpty();
        var companions = ModuleCompileEnvironment.CompanionsFor([Playerbots, Aliased]);
        companions.Should().ContainSingle(item => item.Id == "mod-companion");
        companions[0].Repository.Should().Contain("mod-companion");
    }

    [Fact]
    public void CheckoutFolder_uses_profile_alias_when_set()
    {
        ModuleCompileEnvironment.CheckoutFolder(Aliased)
            .Should()
            .Be("mod-native-folder");
        ModuleCompileEnvironment.CheckoutFolder(OllamaBuddy)
            .Should()
            .Be("mod-ollama-bot-buddy");
        ModuleCompileEnvironment.CheckoutFolder(Playerbots)
            .Should()
            .Be("mod-playerbots");
        ModuleCompileEnvironment.CheckoutFolder("mod-playerbots")
            .Should()
            .Be("mod-playerbots");
    }

    [Fact]
    public void SameGitRepository_ignores_dot_git_and_trailing_slash()
    {
        ModuleCompileEnvironment.SameGitRepository(
                "https://github.com/example/mod-example.git",
                "https://github.com/example/mod-example/")
            .Should()
            .BeTrue();
        ModuleCompileEnvironment.SameGitRepository(
                "https://github.com/example/mod-example",
                "https://github.com/other/mod-example")
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ModuleDirectoriesToKeep_retains_checkout_alias_and_companion_folder()
    {
        ModuleCompileEnvironment.ModuleDirectoriesToKeep([Aliased])
            .Should()
            .BeEquivalentTo(["mod-native-folder", "mod-companion"]);
        ModuleCompileEnvironment.ModuleDirectoriesToKeep([OllamaBuddy])
            .Should()
            .BeEquivalentTo(["mod-ollama-bot-buddy"]);
    }

    [Fact]
    public void ConflictingPairs_deduplicates_mutual_conflicts()
    {
        var pairs = ModuleCompileEnvironment.ConflictingPairs([Aliased, Incompatible]);
        pairs.Should().ContainSingle();
        pairs[0].LeftId.Should().Be("mod-aliased");
        pairs[0].RightId.Should().Be("mod-incompatible");
    }

    [Fact]
    public void RuntimeSidecarsFor_unions_ollama_once_from_either_module()
    {
        var buddy = Module(
            "mod-ollama-bot-buddy",
            new ModuleCompileProfile { RuntimeSidecars = [OllamaSidecar.Default] });
        var chat = Module(
            "mod-ollama-chat",
            new ModuleCompileProfile { RuntimeSidecars = [OllamaSidecar.Default] });

        ModuleCompileEnvironment.RuntimeSidecarsFor([Playerbots]).Should().BeEmpty();
        ModuleCompileEnvironment.RuntimeSidecarsFor([buddy])
            .Should()
            .ContainSingle(item => item.ServiceName == "ollama");
        ModuleCompileEnvironment.RuntimeSidecarsFor([buddy, chat])
            .Should()
            .ContainSingle(item => item.ServiceName == "ollama");
        ModuleCompileEnvironment.HasOllamaSidecar(ModuleCompileEnvironment.RuntimeSidecarsFor([chat]))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void ExtraAptPackagesFor_unions_markdown_tokens_without_removing_hook_packages()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-md-deps-" + Guid.NewGuid().ToString("N"));
        var moduleDir = Path.Combine(root, "mod-example");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllText(Path.Combine(moduleDir, "README.md"), """
            ## Dependencies
            - cURL (libcurl)
            - Qdrant vector DB
            - Ollama
            """);

        try
        {
            var module = Module("mod-example", new ModuleCompileProfile
            {
                ExtraAptPackages = ["nlohmann-json3-dev"],
            });
            var packages = ModuleCompileEnvironment.ExtraAptPackagesFor([module], root);
            packages.Should().Contain("nlohmann-json3-dev");
            packages.Should().Contain("libcurl4-openssl-dev");
            packages.Should().NotContain(item => item.Contains("qdrant", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Install_hooks_declare_ollama_and_dungeon_sim_compile_profiles()
    {
        new OllamaBotBuddyInstallHook().Compile.ExtraAptPackages.Should().Equal(OllamaApt);
        new OllamaBotBuddyInstallHook().Compile.Companions.Should().BeEmpty();
        new OllamaBotBuddyInstallHook().Compile.RuntimeSidecars.Should()
            .ContainSingle(item => item.ServiceName == OllamaSidecar.ServiceName);

        new OllamaChatInstallHook().Compile.ExtraAptPackages.Should().BeEmpty();
        new OllamaChatInstallHook().Compile.RuntimeSidecars.Should()
            .ContainSingle(item => item.ServiceName == OllamaSidecar.ServiceName);

        var sim = new PlayerbotDungeonSimInstallHook().Compile;
        sim.BranchPins.Should().ContainSingle(pin =>
            pin.ModuleId == "mod-dungeon-clear" && pin.Branch == "auto-playerbots");
    }

    [Fact]
    public void Every_ai_chat_hook_conflicts_with_the_other_two()
    {
        var profiles = new Dictionary<string, ModuleCompileProfile>
        {
            [OllamaChatInstallHook.CatalogId] = new OllamaChatInstallHook().Compile,
            [OllamaBotBuddyInstallHook.CatalogId] = new OllamaBotBuddyInstallHook().Compile,
            [LlmChatterInstallHook.CatalogId] = new LlmChatterInstallHook().Compile,
        };

        foreach (var (id, profile) in profiles)
        {
            profile.ConflictsWith
                .Should()
                .BeEquivalentTo(profiles.Keys.Where(other => other != id), $"{id} must exclude the others");
        }
    }

    [Fact]
    public void Llm_chatter_starts_the_shared_ollama_sidecar_and_its_own_bridge()
    {
        var sidecars = new LlmChatterInstallHook().Compile.RuntimeSidecars;

        sidecars.Should().Contain(item => item.ServiceName == OllamaSidecar.ServiceName);
        ModuleCompileEnvironment.HasLlmChatterBridge(sidecars).Should().BeTrue();
        ModuleCompileEnvironment.HasLlmChatterBridge(new OllamaChatInstallHook().Compile.RuntimeSidecars)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void InjectExtraBuildPackages_adds_run_in_build_stage_only()
    {
        const string dockerfile = """
            FROM ubuntu:22.04 AS skeleton
            RUN apt-get update && apt-get install -y curl

            FROM skeleton AS build
            ARG CTYPE=Release
            RUN apt-get update && apt-get install -y --no-install-recommends clang cmake

            FROM skeleton AS runtime
            RUN apt-get install -y libmysqlclient21
            """;

        var updated = ModuleCompileEnvironment.InjectExtraBuildPackages(
            dockerfile,
            ["libcurl4-openssl-dev", "nlohmann-json3-dev"]);

        updated.Should().Contain("# azeroth-platform-extra-build-deps");
        updated.Should().Contain("libcurl4-openssl-dev");
        var buildIndex = updated.IndexOf("FROM skeleton AS build", StringComparison.Ordinal);
        var runtimeIndex = updated.IndexOf("FROM skeleton AS runtime", StringComparison.Ordinal);
        var extraIndex = updated.IndexOf("azeroth-platform-extra-build-deps", StringComparison.Ordinal);
        extraIndex.Should().BeGreaterThan(buildIndex);
        extraIndex.Should().BeLessThan(runtimeIndex);

        var again = ModuleCompileEnvironment.InjectExtraBuildPackages(updated, ["libcurl4-openssl-dev"]);
        RegexCount(again, "azeroth-platform-extra-build-deps").Should().Be(1);

        var stripped = ModuleCompileEnvironment.InjectExtraBuildPackages(updated, []);
        stripped.Should().NotContain("azeroth-platform-extra-build-deps");
        stripped.Should().Contain("FROM skeleton AS runtime");
    }

    [Fact]
    public void InjectExtraBuildPackages_leaves_file_unchanged_without_build_stage()
    {
        const string dockerfile = "FROM ubuntu:22.04\nRUN apt-get install -y clang\n";
        ModuleCompileEnvironment.InjectExtraBuildPackages(dockerfile, ["libcurl4-openssl-dev"])
            .Should()
            .Be(dockerfile);
    }

    [Fact]
    public void DisableExtractorTools_sets_ctools_build_db_only_and_legacy_tools_off()
    {
        const string dockerfile = """
            FROM skeleton AS build
            ARG CTOOLS_BUILD="all"
            RUN cmake /azerothcore -DTOOLS_BUILD="$CTOOLS_BUILD" -DTOOLS=1
            """;

        var updated = ModuleCompileEnvironment.DisableExtractorTools(dockerfile);
        updated.Should().Contain($"ARG CTOOLS_BUILD=\"{ModuleCompileEnvironment.StackToolsBuild}\"");
        updated.Should().NotContain("ARG CTOOLS_BUILD=\"all\"");
        updated.Should().NotContain("ARG CTOOLS_BUILD=\"none\"");
        updated.Should().Contain("-DTOOLS=0");
        updated.Should().NotContain("-DTOOLS=1");

        ModuleCompileEnvironment.DisableExtractorTools(updated).Should().Be(updated);
    }

    [Fact]
    public void DisableExtractorTools_upgrades_previous_none_to_db_only()
    {
        const string dockerfile = """
            FROM skeleton AS build
            ARG CTOOLS_BUILD="none"
            """;

        var updated = ModuleCompileEnvironment.DisableExtractorTools(dockerfile);
        updated.Should().Contain($"ARG CTOOLS_BUILD=\"{ModuleCompileEnvironment.StackToolsBuild}\"");
        updated.Should().NotContain("ARG CTOOLS_BUILD=\"none\"");
    }

    private static int RegexCount(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }

        return count;
    }
}
