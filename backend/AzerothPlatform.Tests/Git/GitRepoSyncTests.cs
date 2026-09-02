using AzerothPlatform.Infrastructure.Services;
using AzerothPlatform.Tests.TestSupport;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Git;

public sealed class GitRepoSyncTests
{
    [Fact]
    public void IsRepository_requires_dot_git()
    {
        using var dir = new TempDir("git-repo-sync");
        GitRepoSync.IsRepository(dir.Path).Should().BeFalse();
        Directory.CreateDirectory(dir.Combine(".git"));
        GitRepoSync.IsRepository(dir.Path).Should().BeTrue();
    }

    [Theory]
    [InlineData("c3a58309075ad557df5dcfa12f85440a57071f8c\trefs/heads/master\n", "c3a58309075ad557df5dcfa12f85440a57071f8c")]
    [InlineData("c3a58309075ad557df5dcfa12f85440a57071f8c refs/heads/master", "c3a58309075ad557df5dcfa12f85440a57071f8c")]
    [InlineData("", null)]
    public void ParseLsRemoteSha_reads_first_object_id(string output, string? expected)
        => GitRepoSync.ParseLsRemoteSha(output).Should().Be(expected);
}
