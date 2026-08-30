using System.Text.RegularExpressions;
using Xunit;

namespace AzerothPlatform.Tests.Armory;

public sealed class ArmoryWallpaperTests
{
    private static readonly Regex AzpWallpaperImageUrlRegex = new(
        @"(class=""azp-wallpaper__img""[^>]*style=""[^""]*background-image:url\(')[^']*(')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void AzpWallpaperImageUrlRegex_replaces_existing_wallpaper_src()
    {
        const string input =
            """<div class="azp-wallpaper__img" style="background-image:url('{{websiteRoot}}/img/azp-wallpaper.jpg');position:absolute;"></div>""";

        var updated = AzpWallpaperImageUrlRegex.Replace(
            input,
            "$1{{websiteRoot}}/img/azp-wallpaper.png$2");

        Assert.Contains("azp-wallpaper.png", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("azp-wallpaper.jpg", updated, StringComparison.Ordinal);
    }
}
