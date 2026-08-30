using AzerothPlatform.ClientContent;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Client;

/// <summary>
/// Covers the change detection the client-server relies on to keep its served manifest honest. The bug
/// these guard against: content changed in the volume, the manifest kept serving the previous version,
/// and launchers went on offering files that had been deleted.
/// </summary>
public sealed class ClientManifestShapeTests : IDisposable
{
    private readonly string _root;
    private readonly string _baseRoot;
    private readonly string _overlayRoot;
    private readonly string _cacheDir;

    public ClientManifestShapeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "azp-client-shape-" + Guid.NewGuid().ToString("N"));
        _baseRoot = Path.Combine(_root, "base");
        _overlayRoot = Path.Combine(_root, "overlay");
        _cacheDir = Path.Combine(_root, "cache");
        Directory.CreateDirectory(Path.Combine(_baseRoot, "Data"));
        Directory.CreateDirectory(Path.Combine(_overlayRoot, "Data"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string[] Roots => [_baseRoot, _overlayRoot];

    private void WriteBase(string relativePath, string content)
    {
        var path = Path.Combine(_baseRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task<ClientManifestResult> BuildAsync() =>
        ClientManifestBuilder.BuildAsync(
            gameRoots: Roots,
            cacheDirectory: _cacheDir,
            managedPrefixes: ClientManifestBuilder.DefaultManagedPrefixes,
            verifyToken: string.Empty);

    [Fact]
    public void ComputeShapeSignature_is_stable_when_nothing_changes()
    {
        WriteBase("Wow.exe", "exe");
        WriteBase("Data/common.MPQ", "stock");

        var first = ClientManifestBuilder.ComputeShapeSignature(Roots);
        var second = ClientManifestBuilder.ComputeShapeSignature(Roots);

        second.Should().Be(first);
    }

    [Fact]
    public void ComputeShapeSignature_changes_when_a_file_is_deleted()
    {
        WriteBase("Wow.exe", "exe");
        WriteBase("Data/junk.txt", "bad file the admin wants gone");
        var before = ClientManifestBuilder.ComputeShapeSignature(Roots);

        File.Delete(Path.Combine(_baseRoot, "Data", "junk.txt"));

        ClientManifestBuilder.ComputeShapeSignature(Roots).Should().NotBe(before);
    }

    [Fact]
    public void ComputeShapeSignature_changes_when_a_file_is_added()
    {
        WriteBase("Wow.exe", "exe");
        var before = ClientManifestBuilder.ComputeShapeSignature(Roots);

        WriteBase("Data/patch-K.MPQ", "new patch");

        ClientManifestBuilder.ComputeShapeSignature(Roots).Should().NotBe(before);
    }

    [Fact]
    public void ComputeShapeSignature_changes_when_a_file_is_replaced()
    {
        WriteBase("Data/patch-K.MPQ", "first revision");
        var before = ClientManifestBuilder.ComputeShapeSignature(Roots);

        WriteBase("Data/patch-K.MPQ", "a longer second revision");

        ClientManifestBuilder.ComputeShapeSignature(Roots).Should().NotBe(before);
    }

    [Fact]
    public void ComputeShapeSignature_ignores_bookkeeping_and_player_state()
    {
        WriteBase("Wow.exe", "exe");
        var before = ClientManifestBuilder.ComputeShapeSignature(Roots);

        // The builder writes these itself; if they counted, every scan would invalidate the next one.
        File.WriteAllText(Path.Combine(_baseRoot, ClientManifestBuilder.HashCacheFileName), "{}");
        File.WriteAllText(Path.Combine(_baseRoot, ClientManifestBuilder.ManifestFileName), "{}");
        WriteBase("WTF/Config.wtf", "SET gxResolution \"1920x1080\"");

        ClientManifestBuilder.ComputeShapeSignature(Roots).Should().Be(before);
    }

    [Fact]
    public async Task BuildAsync_reports_the_signature_of_the_tree_it_scanned()
    {
        WriteBase("Wow.exe", "exe");
        WriteBase("Data/common.MPQ", "stock");

        var result = await BuildAsync();

        result.ShapeSignature.Should().Be(ClientManifestBuilder.ComputeShapeSignature(Roots));
    }

    [Fact]
    public async Task Deleting_a_file_drops_it_from_the_manifest_and_changes_the_version()
    {
        WriteBase("Wow.exe", "exe");
        WriteBase("Data/junk.txt", "bad file the admin wants gone");

        var before = await BuildAsync();
        before.Manifest.Files.Should().Contain(f => f.RelativePath == "Data/junk.txt");

        File.Delete(Path.Combine(_baseRoot, "Data", "junk.txt"));
        var after = await BuildAsync();

        after.Manifest.Files.Should().NotContain(f => f.RelativePath == "Data/junk.txt");
        after.Manifest.Version.Should().NotBe(before.Manifest.Version);
        after.ShapeSignature.Should().NotBe(before.ShapeSignature);
    }

    [Fact]
    public async Task A_retained_hash_cache_still_reflects_a_deletion()
    {
        // Rescan deliberately keeps .hashcache.json so a 17 GB client is not rehashed on every change.
        // This proves that keeping it cannot resurrect a deleted file into the manifest.
        WriteBase("Wow.exe", "exe");
        WriteBase("Data/junk.txt", "bad file the admin wants gone");
        await BuildAsync();

        File.Exists(Path.Combine(_cacheDir, ClientManifestBuilder.HashCacheFileName)).Should().BeTrue();

        File.Delete(Path.Combine(_baseRoot, "Data", "junk.txt"));
        var after = await BuildAsync();

        after.Manifest.Files.Should().NotContain(f => f.RelativePath == "Data/junk.txt");
    }
}
