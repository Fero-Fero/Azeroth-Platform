using System.Net;
using System.Net.Sockets;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Normalizes player-facing hosts for AzerothCore's <c>realmlist</c> table. Auth/world containers must
/// be able to resolve the stored address at startup; EC2 public DNS names often fail from inside Docker
/// even though clients on the internet can reach them, so hostnames are resolved to a public IPv4 first.
/// </summary>
public static class RealmlistHostResolver
{
    /// <summary>
    /// Returns an IPv4 literal suitable for <c>realmlist.address</c>. Hostnames are resolved on the
    /// manager host; when resolution fails the original host is returned unchanged.
    /// </summary>
    public static string ResolveForRealmAddress(string host, CancellationToken cancellationToken = default)
    {
        var trimmed = (host ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        if (IPAddress.TryParse(trimmed, out var literal))
        {
            return literal.AddressFamily == AddressFamily.InterNetwork
                ? trimmed
                : trimmed;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(trimmed);
            var ipv4 = addresses
                .Where(static ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .OrderBy(static ip => IsPrivateOrNonRoutableIpv4(ip) ? 1 : 0)
                .FirstOrDefault();

            if (ipv4 is not null)
            {
                return ipv4.ToString();
            }
        }
        catch (SocketException)
        {
            // Fall back to the hostname; callers log when auth still cannot resolve it.
        }
        catch (Exception)
        {
            // Same as above — do not block stack creation/start on DNS hiccups.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return trimmed;
    }

    private static bool IsPrivateOrNonRoutableIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127)
        {
            return true;
        }

        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
        {
            return true;
        }

        if (b[0] == 192 && b[1] == 168)
        {
            return true;
        }

        if (b[0] == 169 && b[1] == 254)
        {
            return true;
        }

        return false;
    }
}
