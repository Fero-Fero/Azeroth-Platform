using AzerothPlatform.ClientContent;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests.Client;

/// <summary>
/// The purge is the one operation that deletes a stack's whole client in a single click, so what it
/// touches is pinned here. Widening it silently — clearing the cache volume outright, or reaching into
/// the launcher build — would cost an operator their branding, news and built launcher with no warning.
/// </summary>
public sealed class ClientServicePurgeTests : IDisposable
{
    private const string StackId = "stack-purge";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "azp-purge-" + Guid.NewGuid().ToString("N"));
    private readonly AzerothCoreDbContext _db;
    private readonly ServiceProvider _provider;
    private readonly Mock<IRemoteEngineService> _engine = new(MockBehavior.Loose);

    private readonly List<string> _clearedVolumes = [];
    private readonly List<(string Volume, string[] Paths)> _deletedPaths = [];

    public ClientServicePurgeTests()
    {
        _db = CreateDbContext();
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        _provider = services.BuildServiceProvider();

        _engine
            .Setup(e => e.ClearVolumeContentsAsync(
                It.IsAny<ManagedStackEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<ManagedStackEntity, string, CancellationToken>((_, volume, _) => _clearedVolumes.Add(volume))
            .Returns(Task.CompletedTask);

        _engine
            .Setup(e => e.DeleteVolumePathsAsync(
                It.IsAny<ManagedStackEntity>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<ManagedStackEntity, string, IEnumerable<string>, CancellationToken>(
                (_, volume, paths, _) => _deletedPaths.Add((volume, paths.ToArray())))
            .Returns(Task.CompletedTask);

        _engine
            .Setup(e => e.GetVolumeTreeSummaryAsync(
                It.IsAny<ManagedStackEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VolumeTreeSummary { VolumeExists = true });
    }

    [Fact]
    public async Task Purge_clears_the_base_and_overlay_volumes_and_nothing_else()
    {
        await CreateService().PurgeClientContentAsync(StackId);

        _clearedVolumes.Should().BeEquivalentTo(
            [$"acore-{StackId}-client-base", $"acore-{StackId}-client-overlay"]);
    }

    [Fact]
    public async Task Purge_leaves_the_launcher_build_alone()
    {
        await CreateService().PurgeClientContentAsync(StackId);

        var touched = _clearedVolumes.Concat(_deletedPaths.Select(d => d.Volume));
        touched.Should().NotContain($"acore-{StackId}-launcher-dist");
    }

    [Fact]
    public async Task Purge_deletes_only_the_manifest_bookkeeping_from_the_cache_volume()
    {
        await CreateService().PurgeClientContentAsync(StackId);

        _deletedPaths.Should().ContainSingle();
        var (volume, paths) = _deletedPaths[0];
        volume.Should().Be($"acore-{StackId}-client-cache");
        paths.Should().BeEquivalentTo(
            [
                ClientManifestBuilder.HashCacheFileName,
                ClientManifestBuilder.ManifestFileName,
                ClientManifestBuilder.VerifyTokenFileName,
            ]);
    }

    [Fact]
    public async Task Purge_never_clears_the_cache_volume_wholesale()
    {
        // portal.json, branding/ and news/ share that volume and are stack identity, not client content.
        await CreateService().PurgeClientContentAsync(StackId);

        _clearedVolumes.Should().NotContain($"acore-{StackId}-client-cache");
    }

    [Fact]
    public async Task Purge_empties_the_manager_side_overlay_mirror_but_keeps_the_folder()
    {
        var overlayDir = Path.Combine(_root, "builds", StackId, "client", "overlay");
        var dataDir = Path.Combine(overlayDir, "Data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "patch-W.MPQ"), "stale");
        File.WriteAllText(Path.Combine(overlayDir, "loose.txt"), "stale");

        await CreateService().PurgeClientContentAsync(StackId);

        Directory.Exists(overlayDir).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(overlayDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Purge_leaves_the_stacks_patch_definitions_in_place()
    {
        // Recovery after a purge is "re-upload the base, then reapply patches", which only works while
        // the patch sources under migrations/ survive.
        var migrationsDir = Path.Combine(_root, "builds", StackId, "migrations", "3_001_test");
        Directory.CreateDirectory(migrationsDir);
        File.WriteAllText(Path.Combine(migrationsDir, "patch.json"), "{}");

        await CreateService().PurgeClientContentAsync(StackId);

        File.Exists(Path.Combine(migrationsDir, "patch.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Purge_rebuilds_the_launcher_manifest_so_the_change_reaches_players()
    {
        var container = new Mock<IClientContainerService>();

        await CreateService(container).PurgeClientContentAsync(StackId);

        container.Verify(c => c.RescanAsync(StackId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Purge_succeeds_when_the_manager_has_no_overlay_mirror()
    {
        var act = async () => await CreateService().PurgeClientContentAsync(StackId);

        await act.Should().NotThrowAsync();
    }

    private ClientService CreateService(Mock<IClientContainerService>? container = null)
    {
        container ??= new Mock<IClientContainerService>();

        return new ClientService(
            Options.Create(new ClientDistributionOptions { RootPath = Path.Combine(_root, "client") }),
            Options.Create(new ClientDownloadOptions()),
            Options.Create(new DockerOptions { BuildsPath = Path.Combine(_root, "builds") }),
            _engine.Object,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new BaseClientDownloader(NullLogger<BaseClientDownloader>.Instance),
            new Mock<IClientJobService>().Object,
            container.Object,
            NullLogger<ClientService>.Instance);
    }

    private static AzerothCoreDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AzerothCoreDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new AzerothCoreDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        db.ManagedStacks.Add(new ManagedStackEntity
        {
            Id = StackId,
            StackName = "purge-test",
            ClientEnabled = true,
        });
        db.SaveChanges();
        return db;
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
