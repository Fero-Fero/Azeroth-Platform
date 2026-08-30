using AzerothPlatform.Launcher.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Launcher.Tests.Services;

/// <summary>
/// The elevated helper takes its target folder from the command line, so
/// <c>IsAllowedSharedDirectory</c> decides which paths an administrator can be induced to open to
/// BUILTIN\Users. Anything it accepts is a path whose ACL gets weakened for every account on the PC.
/// </summary>
public sealed class InstallPathAccessTests
{
    private static string ProgramData =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void The_machine_install_directory_is_allowed()
    {
        var directory = InstallPathAccess.MachineInstallDirectory("Azeroth Platform");

        InstallPathAccess.IsAllowedSharedDirectory(directory).Should().BeTrue();
    }

    [Fact]
    public void The_user_install_directory_is_allowed()
    {
        var directory = InstallPathAccess.UserInstallDirectory("Azeroth Platform");

        InstallPathAccess.IsAllowedSharedDirectory(directory).Should().BeTrue();
    }

    [Fact]
    public void A_nested_folder_under_an_allowed_root_is_allowed()
    {
        var directory = Path.Combine(ProgramData, "Azeroth Platform", "profiles", "wotlk");

        InstallPathAccess.IsAllowedSharedDirectory(directory).Should().BeTrue();
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_verdict()
    {
        var directory = Path.Combine(ProgramData, "Azeroth Platform") + Path.DirectorySeparatorChar;

        InstallPathAccess.IsAllowedSharedDirectory(directory).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_is_refused(string directory)
    {
        InstallPathAccess.IsAllowedSharedDirectory(directory).Should().BeFalse();
    }

    [Fact]
    public void The_allowed_roots_themselves_are_refused()
    {
        InstallPathAccess.IsAllowedSharedDirectory(ProgramData).Should().BeFalse();
        InstallPathAccess.IsAllowedSharedDirectory(LocalAppData).Should().BeFalse();
    }

    [Fact]
    public void A_path_outside_every_allowed_root_is_refused()
    {
        var outside = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32"
            : "/etc";

        InstallPathAccess.IsAllowedSharedDirectory(outside).Should().BeFalse();
    }

    [Fact]
    public void Traversal_out_of_an_allowed_root_is_refused()
    {
        var escaping = Path.Combine(ProgramData, "Azeroth Platform", "..", "..", "Windows");

        InstallPathAccess.IsAllowedSharedDirectory(escaping).Should().BeFalse();
    }

    [Fact]
    public void A_sibling_that_only_shares_a_name_prefix_is_refused()
    {
        var sibling = ProgramData.TrimEnd(Path.DirectorySeparatorChar) + "Evil";

        InstallPathAccess.IsAllowedSharedDirectory(sibling).Should().BeFalse();
    }

    [Fact]
    public void The_distributors_pinned_directory_is_allowed_exactly_and_below()
    {
        var pinned = OperatingSystem.IsWindows() ? @"D:\Games\MyServer" : "/opt/myserver";
        var child = Path.Combine(pinned, "Data");

        InstallPathAccess.IsAllowedSharedDirectory(pinned, pinned).Should().BeTrue();
        InstallPathAccess.IsAllowedSharedDirectory(child, pinned).Should().BeTrue();
    }

    [Fact]
    public void A_pinned_directory_does_not_widen_the_allow_list_beyond_itself()
    {
        var pinned = OperatingSystem.IsWindows() ? @"D:\Games\MyServer" : "/opt/myserver";
        var unrelated = OperatingSystem.IsWindows() ? @"D:\Games\Something" : "/opt/something";

        InstallPathAccess.IsAllowedSharedDirectory(unrelated, pinned).Should().BeFalse();
    }

    [Fact]
    public async Task Preparing_a_refused_directory_reports_failure_without_creating_it()
    {
        // A filesystem root sits outside every allowed root on both platforms.
        var root = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Environment.SystemDirectory)!
            : "/";
        var refused = Path.Combine(root, $"azp-refused-{Guid.NewGuid():N}");

        var exitCode = await InstallPathAccess.PrepareSharedDirectoryAsync(refused, distributorRoot: null);

        exitCode.Should().NotBe(0);
        Directory.Exists(refused).Should().BeFalse();
    }
}
