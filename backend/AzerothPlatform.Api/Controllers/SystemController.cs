using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
    private readonly MigrationOptions _migrationOptions;

    public SystemController(
        IRemoteEngineService remoteEngine,
        IOptions<MigrationOptions> migrationOptions)
    {
        _remoteEngine = remoteEngine;
        _migrationOptions = migrationOptions.Value;
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
    /// When the manager runs in Docker, interface scans only see container addresses (filtered out). In that
    /// case <see cref="MigrationOptions.RealmlistHost"/> (<c>HOST_LAN_IP</c>) is used as a last resort when
    /// it is a usable private LAN address.
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
        if (!string.IsNullOrWhiteSpace(suggested)
            && !addresses.Contains(suggested, StringComparer.Ordinal))
        {
            addresses.Insert(0, suggested);
        }

        return Ok(new NetworkInfoDto
        {
            Addresses = addresses,
            SuggestedRealmlistHost = suggested,
            SuggestedAdminSourceCidr = ResolveClientSourceCidr()
        });
    }

    /// <summary>Client IP for cloud SG SSH source hints (respects X-Forwarded-For when present).</summary>
    private string? ResolveClientSourceCidr()
    {
        var ip = ResolveClientIpAddress();
        if (ip is null || !IsUsableAdminSourceIp(ip))
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

    private static bool IsUsableAdminSourceIp(IPAddress ip)
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

    private IPAddress? ResolveClientIpAddress()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            foreach (var hop in forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IPAddress.TryParse(hop, out var forwardedIp) && IsUsableAdminSourceIp(forwardedIp))
                {
                    return forwardedIp;
                }
            }
        }

        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is not null && IsUsableAdminSourceIp(remote))
        {
            return remote;
        }

        return null;
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

        // 3. Docker deployments cannot see the host NICs; fall back to the configured realmlist host
        //    (HOST_LAN_IP) when it is a usable private LAN address.
        var configuredHost = _migrationOptions.RealmlistHost?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredHost) && IsUsableLanHost(configuredHost))
        {
            return configuredHost;
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
        [FromBody] RemoteConnectionTestRequestDto request,
        CancellationToken cancellationToken)
    {
        var deployment = request.Deployment ?? new DeploymentConfigDto();
        var result = await _remoteEngine.TestConnectionAsync(
            deployment.ExternalHost,
            deployment.ExternalSshPort,
            deployment.ExternalSshUser,
            deployment.ExternalSshPrivateKey,
            request.Phase,
            cancellationToken);

        return Ok(result);
    }

    /// <summary>Runs first-time Docker provisioning on a remote VPC host over SSH.</summary>
    [HttpPost("provision-remote-host")]
    public async Task<ActionResult<RemoteSetupResultDto>> ProvisionRemoteHost(
        [FromBody] RemoteProvisionRequestDto request,
        CancellationToken cancellationToken)
    {
        var deployment = request.Deployment ?? new DeploymentConfigDto();
        var options = request.Options ?? new RemoteSetupOptionsDto();
        var result = await _remoteEngine.ProvisionRemoteHostAsync(
            deployment.ExternalHost,
            deployment.ExternalSshPort,
            deployment.ExternalSshUser,
            deployment.ExternalSshPrivateKey,
            options,
            cancellationToken);

        return Ok(result);
    }

    /// <summary>Catalog of VPC security roles for external stack deployment.</summary>
    [HttpGet("vpc-security-roles")]
    public ActionResult<VpcSecurityCatalogDto> GetVpcSecurityRoles()
        => Ok(VpcSecurityCatalog.CreateCatalog());

    /// <summary>
    /// Cloud launch script for fresh Ubuntu EC2 instances. Paste into AWS "User data" at launch so
    /// Docker and platform sudo access are ready before the operator connects.
    /// </summary>
    [HttpGet("vpc-launch-user-data")]
    public ActionResult<VpcLaunchUserDataDto> GetVpcLaunchUserData([FromQuery] string? sshUser = "ubuntu")
        => Ok(VpcBootstrapUserData.CreateDto(sshUser ?? "ubuntu"));

    /// <summary>Suggested firewall rules before stack ports are fully known.</summary>
    [HttpGet("vpc-security-profile")]
    public ActionResult<VpcSecurityProfileDto> GetVpcSecurityProfile(
        [FromQuery] string host,
        [FromQuery] int authPort = 3724,
        [FromQuery] int worldPort = 8085,
        [FromQuery] int armoryPort = StackNetworkDefaults.DefaultArmoryPort,
        [FromQuery] int clientPort = StackNetworkDefaults.DefaultClientPort,
        [FromQuery] int databasePort = 3306,
        [FromQuery] int soapPort = 7878,
        [FromQuery] int sshPort = 22)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return BadRequest("host is required.");
        }

        return Ok(VpcSecurityCatalog.BuildProfile(
            host.Trim(),
            authPort,
            worldPort,
            armoryPort,
            clientPort,
            databasePort,
            soapPort,
            sshPort));
    }
}
