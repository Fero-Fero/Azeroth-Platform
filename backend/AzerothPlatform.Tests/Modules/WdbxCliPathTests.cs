using AzerothPlatform.Infrastructure.Services.Modules.Install;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class WdbxCliPathTests
{
    [Fact]
    public void SamePath_is_true_for_dbc_copied_onto_itself()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbc = Path.Combine(dir, "SpellMissileMotion.dbc");
            Assert.True(WdbxCli.SamePath(dbc, Path.Combine(dir, "SpellMissileMotion.dbc")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("SpellMissileMotion.txt", "SpellMissileMotion.csv")]
    [InlineData("SpellMissileMotion.csv", "SpellMissileMotion.csv")]
    [InlineData("Item.txt", "Item.csv")]
    public void WdbxExportFileName_uses_csv_extension_wdbx_accepts(string requested, string expected)
    {
        Assert.Equal(expected, WdbxCli.WdbxExportFileName(requested));
    }

    [Fact]
    public void IsMissingDefinitionError_detects_wdbx_console_message()
    {
        Assert.True(WdbxCli.IsMissingDefinitionError(
            stdout: "",
            stderr: "Could not find definition for CharVariations.dbc build 12340."));
        Assert.False(WdbxCli.IsMissingDefinitionError("Exported Spell.dbc", ""));
    }
}
