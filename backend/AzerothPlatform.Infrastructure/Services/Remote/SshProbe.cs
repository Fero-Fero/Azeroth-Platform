namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Interprets OpenSSH probe output. First-connect host-key warnings go to stderr even when the
/// session succeeds, and freshly launched VMs often drop the first few logins while user-data
/// creates the operator user.
/// </summary>
internal static class SshProbe
{
    public static string StripHostKeyWarnings(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        var kept = stderr
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(static line => line.TrimEnd('\r').Trim())
            .Where(static line => line.Length > 0 && !IsHostKeyWarning(line));

        return string.Join('\n', kept).Trim();
    }

    public static bool IsHostKeyWarning(string line)
        => line.StartsWith("Warning: Permanently added ", StringComparison.OrdinalIgnoreCase)
           || line.Contains("to the list of known hosts.", StringComparison.OrdinalIgnoreCase);

    public static bool IsEchoSuccess(int exitCode, string? stdout, string? stderr)
    {
        if (exitCode == 0)
        {
            return true;
        }

        // Windows OpenSSH can exit 255 after accept-new even when the remote `echo ok` ran.
        return string.Equals((stdout ?? string.Empty).Trim(), "ok", StringComparison.Ordinal)
               && string.IsNullOrEmpty(StripHostKeyWarnings(stderr));
    }

    public static bool ShouldRetry(int exitCode, string? stdout, string? stderr)
    {
        if (IsEchoSuccess(exitCode, stdout, stderr))
        {
            return false;
        }

        var message = StripHostKeyWarnings(stderr);
        if (string.IsNullOrEmpty(message))
        {
            return true;
        }

        return IsTransientFailure(message);
    }

    public static string SetupFailureSummary(string? stderr)
    {
        var message = StripHostKeyWarnings(stderr);
        if (string.IsNullOrEmpty(message))
        {
            return "SSH reached the host but the login session did not start. If you just launched the VM, "
                   + "wait 1–2 minutes for user-data, then Verify VPC again.";
        }

        if (IsConnectivityFailure(message))
        {
            return "Cannot reach the VPC over SSH. Check that the instance is running, the host/IP is "
                   + "correct (EC2 public IPs change after stop/start unless you use an Elastic IP), and SSH "
                   + "port 22 is open in your cloud security group from this manager's IP.";
        }

        if (message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH authentication failed. If you just launched the VM, wait for user-data to create "
                   + "azp-admin, then verify again. Otherwise check the SSH user and private key.";
        }

        if (message.Contains("no mutual", StringComparison.OrdinalIgnoreCase)
            || message.Contains("send_pubkey_test", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH reached the host but the key signature was not accepted. Rebuild the manager so RSA "
                   + "sha2 algorithms are offered, or wait for user-data and verify again.";
        }

        if (IsTransientFailure(message))
        {
            return "SSH reached the host but the session dropped. If you just launched the VM, wait 1–2 "
                   + "minutes for user-data (sshd may restart while the operator user is created), then Verify VPC again.";
        }

        return "SSH connection failed. If you just launched the VM, wait for user-data and verify again. "
               + "Otherwise check the SSH user and key, or use Repair host setup.";
    }

    /// <summary>
    /// ssh_config path token. Quote only when the path contains whitespace — Win32 OpenSSH often
    /// treats quotes as part of the filename and then offers no identity.
    /// </summary>
    public static string FormatConfigPath(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/');
        if (normalized.IndexOfAny([' ', '\t']) >= 0)
        {
            return $"\"{normalized.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }

        return normalized;
    }

    public static string DescribeFailure(int exitCode, string? stdout, string? stderr, string host, string user, int port)
    {
        var stripped = StripHostKeyWarnings(stderr);
        if (!string.IsNullOrWhiteSpace(stripped))
        {
            return stripped;
        }

        var stdoutHint = string.IsNullOrWhiteSpace(stdout) ? string.Empty : $" stdout={stdout.Trim()}";
        return $"Could not complete SSH login to {user}@{host}:{port} (exit {exitCode}).{stdoutHint}";
    }

    public static string ExtractUsefulVerbose(string? verbose)
    {
        if (string.IsNullOrWhiteSpace(verbose))
        {
            return string.Empty;
        }

        var useful = verbose
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(static line => line.TrimEnd('\r').Trim())
            .Where(static line => line.Length > 0 && IsUsefulVerboseLine(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (useful.Count == 0)
        {
            return string.Empty;
        }

        const int keep = 20;
        var selected = useful.Count <= keep
            ? useful
            : useful.Skip(useful.Count - keep).ToList();

        var terminal = selected.Any(static line =>
            line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || line.Contains("No more authentication", StringComparison.OrdinalIgnoreCase)
            || line.Contains("no mutual", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Authentication succeeded", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Connection closed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)
            || line.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        if (!terminal)
        {
            var tail = verbose
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(static line => line.TrimEnd('\r').Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith("debug3:", StringComparison.OrdinalIgnoreCase))
                .TakeLast(8);
            foreach (var line in tail)
            {
                if (!selected.Contains(line, StringComparer.Ordinal))
                {
                    selected.Add(line);
                }
            }
        }

        return string.Join('\n', selected);
    }

    private static bool IsUsefulVerboseLine(string line)
        => line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
           || line.Contains("identity file", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Offering public key", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Server accepts key", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Trying private key", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Authentication succeeded", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Authentications that can continue", StringComparison.OrdinalIgnoreCase)
           || line.Contains("No more authentication", StringComparison.OrdinalIgnoreCase)
           || line.Contains("send_pubkey_test", StringComparison.OrdinalIgnoreCase)
           || line.Contains("no mutual", StringComparison.OrdinalIgnoreCase)
           || line.Contains("invalid format", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Load key", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Connection closed", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase)
           || line.Contains("connect to host", StringComparison.OrdinalIgnoreCase)
           || line.Contains("too open", StringComparison.OrdinalIgnoreCase)
           || line.Contains("UNPROTECTED", StringComparison.OrdinalIgnoreCase)
           || line.Contains("bad permissions", StringComparison.OrdinalIgnoreCase)
           || line.Contains("timed out", StringComparison.OrdinalIgnoreCase)
           || line.Contains("type -1", StringComparison.OrdinalIgnoreCase);

    internal static bool IsConnectivityFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
               || message.Contains("no route to host", StringComparison.OrdinalIgnoreCase)
               || message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase)
               || message.Contains("could not resolve", StringComparison.OrdinalIgnoreCase)
               || message.Contains("banner exchange", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
               || message.Contains("host is down", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientFailure(string message)
        => IsConnectivityFailure(message)
           || message.Contains("connection closed", StringComparison.OrdinalIgnoreCase)
           || message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
           || message.Contains("kex_exchange", StringComparison.OrdinalIgnoreCase)
           || message.Contains("no matching", StringComparison.OrdinalIgnoreCase)
           || message.Contains("no mutual", StringComparison.OrdinalIgnoreCase)
           || message.Contains("send_pubkey_test", StringComparison.OrdinalIgnoreCase)
           || message.Contains("connection aborted", StringComparison.OrdinalIgnoreCase)
           || message.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
           || message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);
}
