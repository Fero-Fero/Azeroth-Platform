using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Game;

public sealed class ServerConfigSeederTests
{
    [Fact]
    public void SeedMissingEffectiveConfigs_creates_worldserver_conf_from_dist()
    {
        var etc = Path.Combine(Path.GetTempPath(), "azp-etc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(etc);
        Directory.CreateDirectory(Path.Combine(etc, "modules"));
        File.WriteAllText(Path.Combine(etc, "worldserver.conf.dist"), "Expansion = 2\n");
        File.WriteAllText(Path.Combine(etc, "modules", "playerbots.conf.dist"), "AiPlayerbot.Enabled = 0\n");
        File.WriteAllText(Path.Combine(etc, "modules", "playerbots.conf"), "AiPlayerbot.Enabled = 1\n");

        try
        {
            ServerConfigService.SeedMissingEffectiveConfigs(etc).Should().Be(1);
            File.ReadAllText(Path.Combine(etc, "worldserver.conf")).Should().Be("Expansion = 2\n");
            File.ReadAllText(Path.Combine(etc, "modules", "playerbots.conf")).Should().Be("AiPlayerbot.Enabled = 1\n");
        }
        finally
        {
            Directory.Delete(etc, recursive: true);
        }
    }

    [Fact]
    public void CopyMissingFiles_does_not_overwrite_local_edits()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-etc-copy-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "volume");
        var dest = Path.Combine(root, "local");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "modules"));
        Directory.CreateDirectory(Path.Combine(dest, "modules"));
        File.WriteAllText(Path.Combine(source, "worldserver.conf"), "Expansion = 0\n");
        File.WriteAllText(Path.Combine(source, "modules", "playerbots.conf"), "from-volume\n");
        File.WriteAllText(Path.Combine(dest, "modules", "playerbots.conf"), "local-edit\n");

        try
        {
            ServerConfigService.CopyMissingFiles(source, dest).Should().Be(1);
            File.ReadAllText(Path.Combine(dest, "worldserver.conf")).Should().Be("Expansion = 0\n");
            File.ReadAllText(Path.Combine(dest, "modules", "playerbots.conf")).Should().Be("local-edit\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CopyCheckoutServerDists_flattens_app_templates_into_etc()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-acore-" + Guid.NewGuid().ToString("N"));
        var world = Path.Combine(root, "src", "server", "apps", "worldserver");
        var auth = Path.Combine(root, "src", "server", "apps", "authserver");
        var etc = Path.Combine(root, "env", "dist", "etc");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(auth);
        File.WriteAllText(Path.Combine(world, "worldserver.conf.dist"), "Expansion = 0\n");
        File.WriteAllText(Path.Combine(auth, "authserver.conf.dist"), "LoginDatabaseInfo = \"x\"\n");

        try
        {
            ServerConfigService.CopyCheckoutServerDists(root, etc).Should().Be(2);
            File.ReadAllText(Path.Combine(etc, "worldserver.conf.dist")).Should().Be("Expansion = 0\n");
            File.ReadAllText(Path.Combine(etc, "authserver.conf.dist")).Should().Be("LoginDatabaseInfo = \"x\"\n");
            ServerConfigService.SeedMissingEffectiveConfigs(etc).Should().Be(2);
            File.Exists(Path.Combine(etc, "worldserver.conf")).Should().BeTrue();
            File.Exists(Path.Combine(etc, "authserver.conf")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveStackTempDir_is_enumerable_before_etc_exists()
    {
        var builds = Path.Combine(Path.GetTempPath(), "azp-builds-" + Guid.NewGuid().ToString("N"));
        var stackId = Guid.NewGuid().ToString("N");
        var etc = Path.Combine(builds, stackId, "azerothcore-wotlk", "env", "dist", "etc");
        Directory.Exists(etc).Should().BeFalse();

        try
        {
            var tmp = ServerConfigService.ResolveStackTempDir(builds, stackId, "config-image-tmp");
            tmp.Should().Be(Path.GetFullPath(Path.Combine(builds, stackId, "config-image-tmp")));
            tmp.Should().NotContain($"{Path.DirectorySeparatorChar}..");
            Directory.CreateDirectory(tmp);
            ServerConfigService.HasMatchingFiles(tmp, "*.conf.dist").Should().BeFalse();
            File.WriteAllText(Path.Combine(tmp, "worldserver.conf.dist"), "Expansion = 0\n");
            ServerConfigService.HasMatchingFiles(tmp, "*.conf.dist").Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(builds))
            {
                Directory.Delete(builds, recursive: true);
            }
        }
    }

    [Fact]
    public void HasMatchingFiles_does_not_throw_when_directory_is_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "azp-missing-" + Guid.NewGuid().ToString("N"));
        Directory.Exists(missing).Should().BeFalse();
        ServerConfigService.HasMatchingFiles(missing, "*.conf.dist").Should().BeFalse();
    }
}
