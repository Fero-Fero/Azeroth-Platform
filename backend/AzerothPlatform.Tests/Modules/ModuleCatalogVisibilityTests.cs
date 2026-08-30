using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class ModuleCatalogVisibilityTests
{
    private const string Playerbots = "mod-playerbots";
    private const string LlmChatter = "mod-llm-chatter";
    private const string DungeonSim = "mod-playerbot-dungeon-sim";

    [Fact]
    public void Modules_needing_a_hidden_dependency_are_hidden_too()
    {
        var kept = ModuleCatalogService.DropUnsatisfiableModules(
            [Module(LlmChatter, Playerbots), Module("mod-ale")],
            []);

        kept.Select(module => module.Id).Should().Equal("mod-ale");
    }

    [Fact]
    public void A_bundled_or_required_dependency_keeps_its_dependents_visible()
    {
        var visible = new List<ModuleDto> { Module(LlmChatter, Playerbots) };

        ModuleCatalogService.DropUnsatisfiableModules(visible, [Playerbots])
            .Should()
            .HaveCount(1);
    }

    [Fact]
    public void A_dependency_chain_collapses_in_one_pass()
    {
        var kept = ModuleCatalogService.DropUnsatisfiableModules(
            [Module(DungeonSim, "mod-dungeon-clear"), Module("mod-dungeon-clear", Playerbots)],
            []);

        kept.Should().BeEmpty();
    }

    [Fact]
    public void Requirements_are_matched_without_regard_to_case()
    {
        var kept = ModuleCatalogService.DropUnsatisfiableModules(
            [Module(LlmChatter, "MOD-PLAYERBOTS"), Module(Playerbots)],
            []);

        kept.Should().HaveCount(2);
    }

    private static ModuleDto Module(string id, params string[] requiredModuleIds) => new()
    {
        Id = id,
        Name = id,
        Repository = $"https://github.com/example/{id}",
        Branch = "master",
        IsBuiltIn = true,
        RequiredModuleIds = [.. requiredModuleIds],
    };
}
