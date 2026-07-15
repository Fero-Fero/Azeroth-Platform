using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Host/system helper endpoints used by the create-stack wizard (LAN IP suggestion, remote connection
/// test).
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly IRemoteEngineService _remoteEngine;

    public SystemController(IRemoteEngineService remoteEngine)
    {
        _remoteEngine = remoteEngine;
    }

    /// <summary>
    /// Returns the host's non-loopback IPv4 addresses so the wizard can prefill the realmlist host with
    /// the host's LAN IP (what a client on the same network must target).
    /// </summary>
    /// <remarks>
    /// Resolution order for the suggested host, best first:
    /// <list type="number">
    /// <item>the address the admin used to reach the manager (request host) when it's a usable IP;</item>
    /// <item>a scan of local interfaces, preferring true LAN ranges and never a Docker/VM address.</item>
    /// </list>
    /// The configured realmlist host is intentionally not used here: it can be stale after a laptop changes
    /// networks, and this endpoint powers a "use this computer's IP" action that must reflect live evidence.
    /// </remarks>
    [HttpGet("network")]
    public ActionResult<NetworkInfoDto> GetNetwork()
    {
        var addresses = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var ip = ua.Address;
                if (IPAddress.IsLoopback(ip) || IsDockerOrVmAddress(ip.ToString()))
                {
                    continue;
                }

                var text = ip.ToString();
                if (!addresses.Contains(text))
                {
                    addresses.Add(text);
                }
            }
        }

        var suggested = ResolveSuggestedHost(addresses);

        return Ok(new NetworkInfoDto
        {
            Addresses = addresses,
            SuggestedRealmlistHost = suggested
        });
    }

    /// <summary>
    /// Anonymous marker used by the localhost UI to discover this manager's LAN address. It deliberately
    /// emits its own permissive CORS header because the request is made cross-origin while probing
    /// http://192.168.x.y:{port} candidates from a http://localhost:{port} page.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("lan-probe")]
    public ActionResult<object> LanProbe()
    {
        Response.Headers.AccessControlAllowOrigin = "*";
        return Ok(new { app = "azeroth-platform", probe = "lan-ip", ok = true });
    }

    /// <summary>Picks the best realmlist host from the current request, then scanned interfaces.</summary>
    private string ResolveSuggestedHost(IReadOnlyList<string> scanned)
    {
        // 1. The address the admin actually reached the manager on (e.g. http://192.168.1.95:8080).
        // This must beat HOST_LAN_IP because laptops move and the configured fallback can go stale.
        var requestHost = Request?.Host.Host;
        if (!string.IsNullOrEmpty(requestHost)
            && IPAddress.TryParse(requestHost, out var reqIp)
            && reqIp.AddressFamily == AddressFamily.InterNetwork
            && IsUsableLanHost(requestHost))
        {
            return requestHost;
        }

        // 2. Fall back to a scanned interface, preferring real private LAN ranges (192.168/10) and
        //    never a Docker/VM address (those are already filtered out of `scanned`).
        var scannedHost = scanned.FirstOrDefault(a =>
                              a.StartsWith("192.168.", StringComparison.Ordinal) ||
                              a.StartsWith("10.", StringComparison.Ordinal))
                          ?? scanned.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(scannedHost))
        {
            return scannedHost;
        }

        return string.Empty;
    }

    /// <summary>True when the host is a non-loopback IPv4 not belonging to a Docker/VM range.</summary>
    private static bool IsUsableLanHost(string host) =>
        IPAddress.TryParse(host, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(ip)
        && !IsDockerOrVmAddress(host);

    /// <summary>
    /// Addresses that must never be advertised to clients: link-local (169.254), the Docker Desktop
    /// vpnkit range (192.168.65), and the Docker default bridge pool (172.17–172.31). A container can
    /// only ever see these, so serving one would leave every off-box client unable to connect.
    /// </summary>
    private static bool IsDockerOrVmAddress(string host)
    {
        if (host.StartsWith("169.254.", StringComparison.Ordinal) ||
            host.StartsWith("192.168.65.", StringComparison.Ordinal))
        {
            return true;
        }

        // Docker's default bridge/user-network pool is 172.16.0.0/12 (172.16.x – 172.31.x).
        if (host.StartsWith("172.", StringComparison.Ordinal)
            && host.IndexOf('.', 4) is var dot && dot > 4
            && int.TryParse(host.AsSpan(4, dot - 4), out var secondOctet)
            && secondOctet is >= 16 and <= 31)
        {
            return true;
        }

        return false;
    }

    /// <summary>Probes an external Docker host over SSH using the supplied connection details.</summary>
    [HttpPost("test-remote-connection")]
    public async Task<ActionResult<RemoteConnectionTestResultDto>> TestRemoteConnection(
        [FromBody] DeploymentConfigDto deployment,
        CancellationToken cancellationToken)
    {
        var result = await _remoteEngine.TestConnectionAsync(
            deployment.ExternalHost,
            deployment.ExternalSshPort,
            deployment.ExternalSshUser,
            deployment.ExternalSshPrivateKey,
            cancellationToken);

        return Ok(result);
    }
}
