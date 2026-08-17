using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class SshProbeTests
{
    [Fact]
    public void StripHostKeyWarnings_leaves_real_errors()
    {
        var stderr = """
            Warning: Permanently added '203.0.113.10' (ED25519) to the list of known hosts.
            azp-admin@203.0.113.10: Permission denied (publickey).
            """;

        SshProbe.StripHostKeyWarnings(stderr)
            .Should().Be("azp-admin@203.0.113.10: Permission denied (publickey).");
    }

    [Fact]
    public void StripHostKeyWarnings_empty_when_only_first_connect_noise()
    {
        SshProbe.StripHostKeyWarnings("Warning: Permanently added '203.0.113.10' (ED25519) to the list of known hosts.")
            .Should().BeEmpty();
    }

    [Fact]
    public void IsEchoSuccess_treats_ok_stdout_with_only_host_key_warning_as_success()
    {
        SshProbe.IsEchoSuccess(
                255,
                "ok\n",
                "Warning: Permanently added '203.0.113.10' (ED25519) to the list of known hosts.")
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldRetry_when_stderr_is_only_host_key_warning()
    {
        SshProbe.ShouldRetry(
                255,
                string.Empty,
                "Warning: Permanently added '203.0.113.10' (ED25519) to the list of known hosts.")
            .Should().BeTrue();
    }

    [Fact]
    public void SetupFailureSummary_does_not_point_at_vpc_overview_for_first_connect_noise()
    {
        var summary = SshProbe.SetupFailureSummary(
            "Warning: Permanently added '203.0.113.10' (ED25519) to the list of known hosts.");

        summary.Should().Contain("user-data");
        summary.Should().NotContain("VPC overview");
    }

    [Fact]
    public void FormatConfigPath_does_not_quote_paths_without_spaces()
    {
        SshProbe.FormatConfigPath("/home/testuser/.ssh/id_ed25519")
            .Should().Be("/home/testuser/.ssh/id_ed25519");
    }

    [Fact]
    public void FormatConfigPath_quotes_paths_with_spaces()
    {
        SshProbe.FormatConfigPath("/home/test user/.ssh/id_ed25519")
            .Should().Be("\"/home/test user/.ssh/id_ed25519\"");
    }

    [Fact]
    public void ExtractUsefulVerbose_keeps_identity_and_permission_lines()
    {
        var verbose = """
            debug1: Connecting to 198.51.100.20
            debug1: identity file /tmp/id_ed25519 type -1
            debug1: Offering public key: /tmp/x RSA SHA256:abc
            azp-admin@198.51.100.20: Permission denied (publickey).
            """;

        SshProbe.ExtractUsefulVerbose(verbose)
            .Should().Contain("identity file")
            .And.Contain("Permission denied")
            .And.NotContain("Connecting to");
    }

    [Fact]
    public void ExtractUsefulVerbose_keeps_tail_when_offer_has_no_terminal_error()
    {
        var verbose = """
            debug1: Connecting to 192.0.2.1
            debug1: identity file /home/app/.ssh/id_ed25519 type 0
            debug1: Offering public key: /home/app/.ssh/id_ed25519 RSA SHA256:abc explicit
            debug1: send_pubkey_test: no mutual signature algorithm
            debug1: Authentications that can continue: publickey
            """;

        var useful = SshProbe.ExtractUsefulVerbose(verbose);
        useful.Should().Contain("Offering public key");
        useful.Should().Contain("no mutual signature algorithm");
    }

    [Fact]
    public void DescribeFailure_includes_exit_code_when_stderr_is_only_host_key_warning()
    {
        SshProbe.DescribeFailure(
                255,
                string.Empty,
                "Warning: Permanently added '198.51.100.20' (ED25519) to the list of known hosts.",
                "198.51.100.20",
                "azp-admin",
                22)
            .Should().Contain("exit 255");
    }
}
