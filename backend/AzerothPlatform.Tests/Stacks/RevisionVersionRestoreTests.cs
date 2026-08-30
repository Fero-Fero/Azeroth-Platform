using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Stacks;

public sealed class RevisionVersionRestoreTests
{
    [Fact]
    public void ApplySnapshotMetadata_copies_sha_modules_and_patch_level()
    {
        var stack = new ManagedStackEntity
        {
            CoreCommitSha = "newsha",
            ModuleVersionsJson = """[{"id":"mod","sha":"new"}]""",
            AppliedPatchLevel = 30,
            AppliedPatchesJson = """[{"key":"3.0"}]""",
            IsOutdated = false
        };
        var revision = new StackRevisionEntity
        {
            CoreCommitSha = "oldsha",
            ModuleVersionsJson = """[{"id":"mod","sha":"old"}]""",
            AppliedPatchLevel = 20,
            AppliedPatchesJson = """[{"key":"2.0"}]"""
        };

        RevisionVersionRestore.ApplySnapshotMetadata(stack, revision);

        stack.CoreCommitSha.Should().Be("oldsha");
        stack.ModuleVersionsJson.Should().Be("""[{"id":"mod","sha":"old"}]""");
        stack.AppliedPatchLevel.Should().Be(20);
        stack.AppliedPatchesJson.Should().Be("""[{"key":"2.0"}]""");
    }

    [Fact]
    public void MarkOutdatedWhenCheckFails_sets_core_outdated_when_shas_differ()
    {
        var stack = new ManagedStackEntity
        {
            CoreCommitSha = "oldsha",
            LatestAvailableCoreSha = "newsha",
            IsOutdated = false,
            IsCoreOutdated = false
        };

        RevisionVersionRestore.MarkOutdatedWhenCheckFails(stack);

        stack.IsOutdated.Should().BeTrue();
        stack.IsCoreOutdated.Should().BeTrue();
    }

    [Fact]
    public void MarkOutdatedWhenCheckFails_leaves_core_current_when_shas_match()
    {
        var stack = new ManagedStackEntity
        {
            CoreCommitSha = "abc",
            LatestAvailableCoreSha = "ABC",
            IsOutdated = false,
            IsCoreOutdated = true
        };

        RevisionVersionRestore.MarkOutdatedWhenCheckFails(stack);

        stack.IsOutdated.Should().BeTrue();
        stack.IsCoreOutdated.Should().BeFalse();
    }
}
