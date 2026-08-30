using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Stacks;

public sealed class RevisionCheckpointImagesTests
{
    [Fact]
    public void CheckpointTag_appends_rev_and_revision_id()
    {
        RevisionCheckpointImages.CheckpointTag("localhost/acore/ac-wotlk-worldserver:abc", "deadbeef")
            .Should().Be("localhost/acore/ac-wotlk-worldserver:abc-rev-deadbeef");
    }

    [Fact]
    public void CanonicalTags_include_localhost_acore_and_bridge()
    {
        var tags = RevisionCheckpointImages.CanonicalTags("stack1");
        tags.Should().Contain("localhost/acore/ac-wotlk-worldserver:stack1");
        tags.Should().Contain("acore/ac-wotlk-worldserver:stack1");
        tags.Should().Contain("localhost/acore/ac-llm-chatter-bridge:stack1");
    }

    [Theory]
    [InlineData(StackStatus.Running, true)]
    [InlineData(StackStatus.Starting, true)]
    [InlineData(StackStatus.Initializing, true)]
    [InlineData(StackStatus.Degraded, true)]
    [InlineData(StackStatus.Stopped, false)]
    [InlineData(StackStatus.Building, false)]
    [InlineData(StackStatus.Failed, false)]
    public void BlocksRestoreWhileLive_matches_world_auth_up_states(StackStatus status, bool expected)
    {
        RevisionCheckpointImages.BlocksRestoreWhileLive(status).Should().Be(expected);
    }
}
