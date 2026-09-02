using AzerothPlatform.Infrastructure.Services.Stacks;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Stacks;

public sealed class ClassicBuilderLeftoversTests
{
    [Fact]
    public void IdsToRemove_selects_exited_azerothcore_compile_containers()
    {
        var output = """
            aaa	quirky_swirles	"/bin/sh -c 'git config --global --add safe.directory /azerothcore && cmake /azerothcore -G Ninja'"
            bbb	focused_bassi	"/bin/sh -c 'git config --global --add safe.directory /azerothcore && cmake /azerothcore'"
            """;

        ClassicBuilderLeftovers.IdsToRemove(output).Should().Equal("aaa", "bbb");
    }

    [Fact]
    public void IdsToRemove_skips_buildx_and_unrelated_exited_containers()
    {
        var output = """
            ccc	buildx_buildkit_default	"/usr/bin/buildkitd"
            ddd	azeroth-platform	"dotnet AzerothPlatform.Api.dll"
            eee	adoring_margulis	"/bin/sh -c 'echo hello'"
            """;

        ClassicBuilderLeftovers.IdsToRemove(output).Should().BeEmpty();
    }

    [Fact]
    public void IdsToRemove_is_empty_for_blank_output()
    {
        ClassicBuilderLeftovers.IdsToRemove("").Should().BeEmpty();
        ClassicBuilderLeftovers.IdsToRemove("   \n").Should().BeEmpty();
    }
}
