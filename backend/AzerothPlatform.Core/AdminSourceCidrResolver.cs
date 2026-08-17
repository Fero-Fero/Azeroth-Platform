using System.Net;
using System.Net.Sockets;

namespace AzerothPlatform.Core;

/// <summary>
/// Resolves a public admin SSH source CIDR from an HTTP request (X-Forwarded-For, then remote IP).
/// Used when launch/pick did not receive a browser-detected /32.
/// </summary>
public static class AdminSourceCidrResolver
{
    public static string? FromForwardedAndRemote(string? forwardedFor, IPAddress? remoteIp)
    {
        var ip = ResolveIp(forwardedFor, remoteIp);
        if (ip is null)
        {
            return null;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => $"{ip}/32",
            AddressFamily.InterNetworkV6 => $"{ip}/128",
            _ => null
        };
    }

    public static bool IsUsableAdminSourceIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 10)
        {
            return false;
        }

        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return false;
        }

        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return false;
        }

        return true;
    }

    private static IPAddress? ResolveIp(string? forwardedFor, IPAddress? remoteIp)
    {
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            foreach (var hop in forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IPAddress.TryParse(hop, out var forwardedIp) && IsUsableAdminSourceIp(forwardedIp))
                {
                    return forwardedIp;
                }
            }
        }

        if (remoteIp is not null && IsUsableAdminSourceIp(remoteIp))
        {
            return remoteIp;
        }

        return null;
    }
}
