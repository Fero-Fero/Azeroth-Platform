using System.Reflection;
using AzerothPlatform.Infrastructure.Services;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class ArmoryAccountSyncTests
{
    [Fact]
    public void LiveLayoutRootFiles_includes_account_hub_assets()
    {
        var field = typeof(ArmoryImageService).GetField(
            "LiveLayoutRootFiles",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var files = Assert.IsType<string[]>(field.GetValue(null));

        Assert.Contains("account.hbs", files);
        Assert.Contains("css/account.css", files);
        Assert.Contains("css/guild.css", files);
        Assert.Contains("css/emblems.css", files);
        Assert.Contains("css/icons.css", files);
    }
}
