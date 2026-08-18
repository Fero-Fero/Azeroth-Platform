using AzerothPlatform.Core;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>Cloud integration helpers (SSH key vault, provider connections, instance discovery).</summary>
[Authorize]
[ApiController]
[Route("api/cloud")]
public class CloudController : ControllerBase
{
    private readonly ICloudSshKeyService _cloudSshKeyService;
    private readonly ICloudProviderConnectionService _cloudProviderConnectionService;
    private readonly ICloudLaunchService _cloudLaunchService;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly ICloudSetupDialogService _cloudSetupDialogService;
    private readonly ICloudFirewallService _cloudFirewallService;
    private readonly ICloudAuthOrchestrator _cloudAuthOrchestrator;

    public CloudController(
        ICloudSshKeyService cloudSshKeyService,
        ICloudProviderConnectionService cloudProviderConnectionService,
        ICloudLaunchService cloudLaunchService,
        ICloudAuditService cloudAuditService,
        ICloudSetupDialogService cloudSetupDialogService,
        ICloudFirewallService cloudFirewallService,
        ICloudAuthOrchestrator cloudAuthOrchestrator)
    {
        _cloudSshKeyService = cloudSshKeyService;
        _cloudProviderConnectionService = cloudProviderConnectionService;
        _cloudLaunchService = cloudLaunchService;
        _cloudAuditService = cloudAuditService;
        _cloudSetupDialogService = cloudSetupDialogService;
        _cloudFirewallService = cloudFirewallService;
        _cloudAuthOrchestrator = cloudAuthOrchestrator;
    }

    /// <summary>Lists saved SSH keys (metadata only - private key material is never returned).</summary>
    [HttpGet("ssh-keys")]
    public async Task<ActionResult<IReadOnlyList<CloudSshKeyDto>>> ListSshKeys(CancellationToken cancellationToken)
        => Ok(await _cloudSshKeyService.ListAsync(cancellationToken));

    /// <summary>Stores a new SSH private key encrypted at rest.</summary>
    [HttpPost("ssh-keys")]
    public async Task<ActionResult<CloudSshKeyDto>> CreateSshKey(
        [FromBody] CreateCloudSshKeyRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _cloudSshKeyService.CreateAsync(request, cancellationToken);
            return Created($"/api/cloud/ssh-keys/{created.Id}", created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Removes a saved SSH key from the vault.</summary>
    [HttpDelete("ssh-keys/{id}")]
    public async Task<IActionResult> DeleteSshKey(string id, CancellationToken cancellationToken)
    {
        try
        {
            await _cloudSshKeyService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Downloads a saved SSH private key as PEM for the admin who owns this manager.</summary>
    [HttpGet("ssh-keys/{id}/download")]
    public async Task<ActionResult<CloudSshKeyExportDto>> DownloadSshKey(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _cloudSshKeyService.ExportAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Lists linked cloud provider accounts (tokens are never returned).</summary>
    [HttpGet("connections")]
    public async Task<ActionResult<IReadOnlyList<CloudProviderConnectionDto>>> ListConnections(
        CancellationToken cancellationToken)
        => Ok(await _cloudProviderConnectionService.ListAsync(cancellationToken));

    /// <summary>Links a cloud provider account (token validated and stored encrypted).</summary>
    [HttpPost("connections")]
    public async Task<ActionResult<CloudProviderConnectionDto>> CreateConnection(
        [FromBody] CreateCloudProviderConnectionRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _cloudProviderConnectionService.CreateAsync(request, cancellationToken);
            return Created($"/api/cloud/connections/{created.Id}", created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Removes a linked cloud provider account and revokes OAuth tokens when present.</summary>
    [HttpDelete("connections/{id}")]
    public async Task<IActionResult> DeleteConnection(string id, CancellationToken cancellationToken)
    {
        try
        {
            await _cloudAuthOrchestrator.RevokeAsync(id, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Re-validates stored credentials against the provider API.</summary>
    [HttpPost("connections/{id}/verify")]
    public async Task<ActionResult<CloudConnectionVerifyResultDto>> VerifyConnection(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _cloudProviderConnectionService.VerifyAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Lists discoverable instances (droplets, VMs, etc.) for a linked account.</summary>
    [HttpGet("connections/{id}/instances")]
    public async Task<ActionResult<IReadOnlyList<CloudInstanceDto>>> ListInstances(
        string id,
        [FromQuery] string? region,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _cloudProviderConnectionService.ListInstancesAsync(id, region, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Returns capabilities for the Configure instance dialog (list/create/firewall).</summary>
    [HttpGet("connections/{id}/setup-dialog")]
    public async Task<ActionResult<CloudInstanceSetupDialogDto>> GetSetupDialog(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _cloudSetupDialogService.GetAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Returns provider-specific defaults for launching or bootstrapping a VM.</summary>
    [HttpGet("connections/{id}/launch-defaults")]
    public async Task<ActionResult<CloudLaunchDefaultsDto>> GetLaunchDefaults(
        string id,
        [FromQuery] RemoteHostOs targetOs = RemoteHostOs.Linux,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _cloudLaunchService.GetDefaultsAsync(id, cancellationToken, targetOs));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Returns regions, sizes, and images for the launch form (provider API catalog).</summary>
    [HttpGet("connections/{id}/launch-catalog")]
    public async Task<ActionResult<CloudLaunchCatalogDto>> GetLaunchCatalog(
        string id,
        [FromQuery] string? region,
        [FromQuery] RemoteHostOs targetOs = RemoteHostOs.Linux,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _cloudLaunchService.GetCatalogAsync(id, region, cancellationToken, targetOs));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Lists recent cloud audit events (no secret material).</summary>
    [HttpGet("audit-logs")]
    public async Task<ActionResult<IReadOnlyList<CloudAuditLogDto>>> ListAuditLogs(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
        => Ok(await _cloudAuditService.ListRecentAsync(limit, cancellationToken));

    /// <summary>Creates a new VM with bootstrap user data (DO/GCP/AWS) or bootstraps an existing AWS instance (SSM).</summary>
    [HttpPost("connections/{id}/launch")]
    public async Task<ActionResult<CloudLaunchResultDto>> Launch(
        string id,
        [FromBody] CloudLaunchRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.AdminSourceCidr))
            {
                request.AdminSourceCidr = AdminSourceCidrResolver.FromForwardedAndRemote(
                    Request.Headers["X-Forwarded-For"].FirstOrDefault(),
                    HttpContext.Connection.RemoteIpAddress);
            }

            return Ok(await _cloudLaunchService.LaunchAsync(id, request, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Checks that the instance security group matches the launch network profile.</summary>
    [HttpPost("connections/{id}/firewall-probe")]
    public async Task<ActionResult<CloudFirewallProbeResultDto>> ProbeFirewall(
        string id,
        [FromBody] CloudFirewallProbeRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _cloudFirewallService.ProbeLaunchSecurityGroupAsync(id, request, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
