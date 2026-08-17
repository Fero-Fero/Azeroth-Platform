using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.RemoteHost;

/// <summary>Interprets uname / Windows %OS% probes from an SSH session.</summary>
internal static class RemoteHostOsProbe
{
    public static RemoteHostOs? Interpret(string? unameOutput, string? windowsOsEnv)
    {
        var uname = (unameOutput ?? string.Empty).Trim();
        if (LooksLikeLinux(uname))
        {
            return RemoteHostOs.Linux;
        }

        if (LooksLikeWindowsKernel(uname))
        {
            return RemoteHostOs.Windows;
        }

        var windows = (windowsOsEnv ?? string.Empty).Trim();
        if (windows.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteHostOs.Windows;
        }

        return null;
    }

    private static bool LooksLikeLinux(string uname)
        => uname.Contains("Linux", StringComparison.OrdinalIgnoreCase)
           || uname.Equals("Darwin", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeWindowsKernel(string uname)
        => uname.Contains("MINGW", StringComparison.OrdinalIgnoreCase)
           || uname.Contains("MSYS", StringComparison.OrdinalIgnoreCase)
           || uname.Contains("CYGWIN", StringComparison.OrdinalIgnoreCase)
           || uname.Contains("Windows_NT", StringComparison.OrdinalIgnoreCase)
           || (uname.Contains("NT", StringComparison.OrdinalIgnoreCase)
               && uname.Contains('_'));
}
