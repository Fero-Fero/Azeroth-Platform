using System.Text.RegularExpressions;
using AzerothPlatform.Infrastructure.Services;
using Xunit;

namespace AzerothPlatform.Tests;

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

    [Fact]
    public void StripDeprecatedVideoWallpaperMarkup_removes_legacy_bg_video_block()
    {
        const string input = """
            <body>
            <video class="bg-video" autoplay="" muted="" loop="" playsinline="" preload="auto" disablepictureinpicture="" aria-hidden="true" poster="/img/bg/wallpaper.jpg">
            	<source src="/img/bg/wallpaper.mp4" type="video/mp4">
            </video>
            {{> armory-navbar}}
            </body>
            """;

        var updated = ArmoryImageService.StripDeprecatedVideoWallpaperMarkup(input);

        Assert.DoesNotContain("bg-video", updated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wallpaper.mp4", updated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{{> armory-navbar}}", updated, StringComparison.Ordinal);
    }
}
