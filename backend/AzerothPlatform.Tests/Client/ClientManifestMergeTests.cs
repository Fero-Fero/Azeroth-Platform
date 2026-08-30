using AzerothPlatform.ClientContent;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Client;

/// <summary>
/// Covers how the uploaded base client and the server-wide-progression overlay are merged into one
/// manifest: they coexist when their paths differ, the newest file wins when they collide, and the
/// stock Blizzard archives are never served from the overlay.
/// </summary>
public sealed class ClientManifestMergeTests : IDisposable
{
    private static readonly DateTime Older = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root;
    private readonly string _baseRoot;
    private readonly string _overlayRoot;

    public ClientManifestMergeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "azp-client-merge-" + Guid.NewGuid().ToString("N"));
        _baseRoot = Path.Combine(_root, "base");
        _overlayRoot = Path.Combine(_root, "overlay");
        Directory.CreateDirectory(_baseRoot);
        Directory.CreateDirectory(_overlayRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string[] Roots => [_baseRoot, _overlayRoot];

    private void Write(string root, string relativePath, string content, DateTime modifiedUtc)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    private void WriteBase(string relativePath, string content, DateTime modifiedUtc) =>
        Write(_baseRoot, relativePath, content, modifiedUtc);

    private void WriteOverlay(string relativePath, string content, DateTime modifiedUtc) =>
        Write(_overlayRoot, relativePath, content, modifiedUtc);

    [Fact]
    public void Files_that_do_not_collide_are_all_served()
    {
        WriteBase("Wow.exe", "exe", Older);
        WriteBase("Data/common.MPQ", "stock", Older);
        WriteOverlay("Data/patch-K.MPQ", "progression", Newer);

        var resolved = ClientManifestBuilder.ResolveContentFiles(Roots);

        resolved.Keys.Should().BeEquivalentTo("Wow.exe", "Data/common.MPQ", "Data/patch-K.MPQ");
    }

    [Fact]
    public void An_uploaded_client_does_not_revert_a_newer_published_patch()
    {
        // The exact scenario that matters: patch published, then the admin re-uploads a base client
        // that happens to carry an older copy of the same file.
        WriteBase("Data/patch-K.MPQ", "from the uploaded client", Older);
        WriteOverlay("Data/patch-K.MPQ", "from server-wide progression", Newer);

        var resolved = ClientManifestBuilder.ResolveContentFiles(Roots);

        resolved["Data/patch-K.MPQ"].Should().StartWith(_overlayRoot);
    }

    [Fact]
    public void A_newer_uploaded_file_wins_over_a_stale_overlay_copy()
    {
        WriteBase("Data/patch-K.MPQ", "freshly uploaded", Newer);
        WriteOverlay("Data/patch-K.MPQ", "stale published copy", Older);

        var resolved = ClientManifestBuilder.ResolveContentFiles(Roots);

        resolved["Data/patch-K.MPQ"].Should().StartWith(_baseRoot);
    }

    [Fact]
    public void Identical_timestamps_resolve_to_the_overlay()
    {
        WriteBase("Data/patch-K.MPQ", "base", Newer);
        WriteOverlay("Data/patch-K.MPQ", "overlay", Newer);

        var resolved = ClientManifestBuilder.ResolveContentFiles(Roots);

        resolved["Data/patch-K.MPQ"].Should().StartWith(_overlayRoot);
    }

    [Theory]
    [InlineData("Data/common.mpq")]
    [InlineData("Data/common-2.mpq")]
    [InlineData("Data/expansion.mpq")]
    [InlineData("Data/lichking.mpq")]
    [InlineData("Data/patch.mpq")]
    [InlineData("Data/patch-2.mpq")]
    [InlineData("Data/patch-3.mpq")]
    public void The_overlay_never_shadows_a_stock_archive_even_when_newer(string stockPath)
    {
        WriteBase(stockPath, "the real thing", Older);
        WriteOverlay(stockPath, "must not win", Newer);

        var resolved = ClientManifestBuilder.ResolveContentFiles(Roots);

        resolved[stockPath].Should().StartWith(_baseRoot);
    }

    [Fact]
    public void Overlay_content_survives_when_the_base_root_is_replaced_wholesale()
    {
        WriteOverlay("Data/patch-K.MPQ", "progression", Newer);
        WriteBase("Wow.exe", "old client", Older);

        // Stand in for a base re-upload: the base root is emptied and refilled, the overlay untouched.
        Directory.Delete(_baseRoot, recursive: true);
        Directory.CreateDirectory(_baseRoot);
        WriteBase("Wow.exe", "new client", Newer);

        var resolved = ClientManifestBuilder.ResolveContentFiles(Roots);

        resolved.Should().ContainKey("Data/patch-K.MPQ");
        resolved["Data/patch-K.MPQ"].Should().StartWith(_overlayRoot);
    }
}
