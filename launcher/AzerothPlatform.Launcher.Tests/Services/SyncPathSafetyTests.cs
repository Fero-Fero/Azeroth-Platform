using AzerothPlatform.Launcher.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Launcher.Tests.Services;

/// <summary>
/// Every path the launcher writes to or deletes goes through <c>ToLocalPath</c>. It is the last line of
/// defense between a hostile manifest (or a tampered local state file) and the rest of the player's disk.
/// </summary>
public sealed class SyncPathSafetyTests
{
    private static readonly string Install =
        OperatingSystem.IsWindows() ? @"C:\Games\WoW" : "/games/wow";

    [Theory]
    [InlineData("Wow.exe")]
    [InlineData("Data/common.MPQ")]
    [InlineData(@"Data\enUS\realmlist.wtf")]
    public void A_relative_path_resolves_under_the_install_directory(string relativePath)
    {
        var resolved = SyncService.ToLocalPath(Install, relativePath);

        resolved.Should().StartWith(Path.GetFullPath(Install) + Path.DirectorySeparatorChar);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../outside.txt")]
    [InlineData("Data/../../outside.txt")]
    [InlineData(@"Data\..\..\outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    public void An_escaping_path_is_rejected(string relativePath)
    {
        var resolve = () => SyncService.ToLocalPath(Install, relativePath);

        resolve.Should().Throw<InvalidOperationException>();
    }
}
