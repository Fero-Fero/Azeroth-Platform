using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Client;

public sealed class ClientUploadStagingTests
{
    [Fact]
    public void UploadStagingRoot_lives_under_the_client_data_root()
    {
        var options = new ClientDistributionOptions { RootPath = Path.Combine("/app", "data", "client") };
        var staging = options.UploadStagingRoot("stack1");
        var expectedPrefix = Path.GetFullPath(options.RootPath);
        Assert.StartsWith(expectedPrefix, Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upload-staging", staging.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("stack1", staging, StringComparison.Ordinal);
    }
}

public sealed class StagedClientArchiveTests
{
    [Fact]
    public void A_work_volume_token_round_trips()
    {
        var staged = StagedClientArchive.InWorkVolume("acore-client-upload-abc123");

        var parsed = StagedClientArchive.Parse(staged.ToString());

        parsed.Kind.Should().Be(StagedClientArchiveKind.WorkVolume);
        parsed.Location.Should().Be("acore-client-upload-abc123");
    }

    [Fact]
    public void A_manager_disk_token_round_trips()
    {
        var path = Path.Combine("/app", "data", "client", "upload-staging", "s1", "upload.archive");
        var staged = StagedClientArchive.OnManagerDisk(path);

        var parsed = StagedClientArchive.Parse(staged.ToString());

        parsed.Kind.Should().Be(StagedClientArchiveKind.ManagerDisk);
        parsed.Location.Should().Be(path);
    }

    [Fact]
    public void An_unprefixed_token_is_read_as_a_manager_disk_path()
    {
        // Tokens issued before work-volume staging existed were bare file paths; a job queued across an
        // upgrade must still resolve rather than being mistaken for a volume name.
        var parsed = StagedClientArchive.Parse("/app/data/client/upload-staging/s1/upload.archive");

        parsed.Kind.Should().Be(StagedClientArchiveKind.ManagerDisk);
        parsed.Location.Should().Be("/app/data/client/upload-staging/s1/upload.archive");
    }

    [Fact]
    public void A_windows_path_is_not_mistaken_for_a_volume_token()
    {
        var parsed = StagedClientArchive.Parse(@"D:\staging\upload.archive");

        parsed.Kind.Should().Be(StagedClientArchiveKind.ManagerDisk);
        parsed.Location.Should().Be(@"D:\staging\upload.archive");
    }
}
