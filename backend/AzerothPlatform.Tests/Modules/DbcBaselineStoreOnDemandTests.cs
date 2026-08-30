using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Services.DbcStore;
using AzerothPlatform.Infrastructure.Services.Modules.Install;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class DbcBaselineStoreOnDemandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "azp-dbc-store-" + Guid.NewGuid().ToString("N"));

    public DbcBaselineStoreOnDemandTests() =>
        Directory.CreateDirectory(Path.Combine(_root, "builds"));

    [Fact]
    public void IsReady_is_always_true_without_bulk_sync()
    {
        var store = Create(new Mock<IWdbxCli>().Object);
        store.IsReady().Should().BeTrue();
        store.GetStatus().Ready.Should().BeTrue();
        store.GetStatus().Tag.Should().Be("on-demand");
        store.GetStatus().TableCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureTableCsv_exports_once_then_reuses_newer_cache()
    {
        var dbc = Path.Combine(_root, "SkillLine.dbc");
        await File.WriteAllTextAsync(dbc, "ID,Name\r\n1,Vanilla\r\n");
        var wdbx = new Mock<IWdbxCli>();
        wdbx.Setup(w => w.ExportDbcToCsvAsync(dbc, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, csv, ct) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
                return File.WriteAllTextAsync(csv, "ID,Name\r\n1,Vanilla\r\n", ct);
            });

        var store = Create(wdbx.Object);
        var first = await store.EnsureTableCsvAsync("SkillLine", dbc);
        var second = await store.EnsureTableCsvAsync("SkillLine", dbc);

        first.Should().NotBeNull();
        second.Should().Be(first);
        File.Exists(first!).Should().BeTrue();
        wdbx.Verify(w => w.ExportDbcToCsvAsync(dbc, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        store.GetStatus().TableCount.Should().Be(1);
    }

    [Fact]
    public async Task EnsureTableCsv_returns_null_when_wdbx_has_no_definition()
    {
        var dbc = Path.Combine(_root, "CharVariations.dbc");
        await File.WriteAllTextAsync(dbc, "unused");
        var wdbx = new Mock<IWdbxCli>();
        wdbx.Setup(w => w.ExportDbcToCsvAsync(dbc, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WdbxDefinitionMissingException("CharVariations.dbc", "build 12340"));

        var store = Create(wdbx.Object);
        var csv = await store.EnsureTableCsvAsync("CharVariations", dbc);

        csv.Should().BeNull();
        store.FindTableCsv("CharVariations").Should().BeNull();
    }

    [Fact]
    public async Task EnsureTableCsv_falls_back_to_existing_cache_when_dbc_is_missing()
    {
        var store = Create(new Mock<IWdbxCli>().Object);
        var cached = Path.Combine(store.StoreDirectory!, CsvNormalizer.TableFileName("Spell"));
        await File.WriteAllTextAsync(cached, "ID,Mana\r\n1,100\r\n");

        var csv = await store.EnsureTableCsvAsync("Spell", Path.Combine(_root, "missing", "Spell.dbc"));
        csv.Should().Be(cached);
    }

    [Fact]
    public async Task Sync_force_clears_cached_csvs()
    {
        var store = Create(new Mock<IWdbxCli>().Object);
        var cached = Path.Combine(store.StoreDirectory!, CsvNormalizer.TableFileName("Item"));
        await File.WriteAllTextAsync(cached, "ID,Name\r\n1,Sword\r\n");

        await store.SyncAsync(force: true, onProgress: null);
        File.Exists(cached).Should().BeFalse();
        store.GetStatus().TableCount.Should().Be(0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private DbcBaselineStore Create(IWdbxCli wdbx) =>
        new(
            Options.Create(new DockerOptions { BuildsPath = Path.Combine(_root, "builds") }),
            wdbx,
            NullLogger<DbcBaselineStore>.Instance);
}
