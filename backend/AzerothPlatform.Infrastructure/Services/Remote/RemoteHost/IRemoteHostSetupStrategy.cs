using AzerothPlatform.Core.Contracts;
using System.Net;
using System.Text.RegularExpressions;

namespace AzerothPlatform.Infrastructure.Services.RemoteHost;

/// <summary>Short-lived SSH session for Linux host setup.</summary>
public interface IRemoteSshSession
{
    string ContextName { get; }

    string Host { get; }

    int Port { get; }

    string User { get; }

    Task<(int ExitCode, string StdOut, string StdErr)> RunBashAsync(
        string command,
        CancellationToken cancellationToken,
        int connectTimeoutSeconds = 30);

    Task<(int ExitCode, string StdOut, string StdErr)> RunPowerShellAsync(
        string script,
        CancellationToken cancellationToken,
        int connectTimeoutSeconds = 90);
}

public interface IRemoteHostSetupStrategy
{
    RemoteHostOs Os { get; }

    Task ProbePrerequisitesAsync(
        IRemoteSshSession session,
        List<RemotePrerequisiteCheckDto> checks,
        CancellationToken cancellationToken);

    Task<RemoteSetupResultDto> ProvisionAsync(
        IRemoteSshSession session,
        RemoteSetupOptionsDto options,
        List<RemotePrerequisiteCheckDto> steps,
        CancellationToken cancellationToken);

    Task<RemoteSetupResultDto?> ApplyFirewallAsync(
        IRemoteSshSession session,
        RemoteSetupOptionsDto options,
        List<RemotePrerequisiteCheckDto> steps,
        CancellationToken cancellationToken);

    Task ProbeFirewallAsync(
        IRemoteSshSession session,
        VpcSecurityProfileDto profile,
        VpcFirewallStatusDto result,
        CancellationToken cancellationToken);
}

internal static class RemoteHostSetupSupport
{
    private static readonly Regex PowerShellCliXmlError = new(
        @"<S\s+N=""Error"">(?<msg>.*?)</S>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static RemoteSetupResultDto Fail(List<RemotePrerequisiteCheckDto> steps, string message)
        => new() { Success = false, Message = message, Steps = steps };

    /// <summary>
    /// OpenSSH + Windows PowerShell -NonInteractive writes progress records to stderr as CLIXML
    /// ("Preparing modules for first use"). That is not an install failure.
    /// </summary>
    public static string StripPowerShellCliXml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var raw = text.Trim();
        if (!raw.Contains("#< CLIXML", StringComparison.OrdinalIgnoreCase)
            && !raw.Contains("<Objs ", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        var errors = new List<string>();
        foreach (Match match in PowerShellCliXmlError.Matches(raw))
        {
            var msg = DecodeCliXmlText(match.Groups["msg"].Value);
            if (msg.Length == 0
                || msg.Contains("Preparing modules for first use", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            errors.Add(msg);
        }

        return string.Join('\n', errors);
    }

    private static string DecodeCliXmlText(string value)
    {
        var text = WebUtility.HtmlDecode(value ?? string.Empty)
            .Replace("_x000A_", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("_x000D_", "\r", StringComparison.OrdinalIgnoreCase)
            .Replace("_x0009_", "\t", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return text;
    }

    public static string FormatError(string stderr, string stdout)
    {
        var err = StripPowerShellCliXml(stderr).Trim();
        if (!string.IsNullOrEmpty(err))
        {
            return TruncateError(err);
        }

        var outText = StripPowerShellCliXml(stdout).Trim();
        return string.IsNullOrEmpty(outText)
            ? "Remote command failed."
            : TruncateError(outText);
    }

    private static string TruncateError(string text)
        => text.Length > 800 ? text[..800] + "…" : text;

    public static IReadOnlyList<int> CollectPlayerWebPorts(RemoteSetupOptionsDto options)
    {
        var ports = new HashSet<int> { options.AuthServerPort, options.WorldServerPort };
        if (options.ArmoryPort > 0)
        {
            ports.Add(options.ArmoryPort);
        }

        if (options.ClientPort > 0)
        {
            ports.Add(options.ClientPort);
        }

        return ports.OrderBy(port => port).ToArray();
    }
}
