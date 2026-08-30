using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Realm management endpoints for AzerothCore stacks (backed by <c>acore_auth.realmlist</c>).
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/realms")]
public class RealmsController : ControllerBase
{
    private readonly IRealmService _realmService;
    private readonly IStackService _stackService;

    public RealmsController(
        IRealmService realmService,
        IStackService stackService)
    {
        _realmService = realmService;
        _stackService = stackService;
    }

    /// <summary>
    /// Get all realms defined for a stack.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<RealmDto>>> GetRealms(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var realms = await _realmService.GetRealmsAsync(stackId, cancellationToken);
            return Ok(realms);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to retrieve realms: {ex.Message}" });
        }
    }

    /// <summary>
    /// Create a new realm in the stack's realmlist.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<RealmDto>> CreateRealm(
        string stackId,
        [FromBody] CreateRealmRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var realm = await _realmService.CreateRealmAsync(stackId, request, cancellationToken);
            return Ok(realm);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to create realm: {ex.Message}" });
        }
    }

    /// <summary>
    /// Set the player-facing host/IP for this stack. Persists it as the stack's realmlist host override,
    /// applies it to the live realmlist, regenerates runtime config, and refreshes launcher/armory/client
    /// services that consume the public host.
    /// </summary>
    [HttpPut("address")]
    public async Task<ActionResult<SetRealmAddressResponseDto>> SetRealmAddress(
        string stackId,
        [FromBody] SetRealmAddressRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _stackService.BeginApplyStackPublicHostAsync(stackId, request.Host, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to set realm address: {ex.Message}" });
        }
    }

    /// <summary>
    /// Update a realm's name, type, flags, timezone, and allowed security level.
    /// </summary>
    [HttpPut("{realmId:int}")]
    public async Task<ActionResult<RealmDto>> UpdateRealm(
        string stackId,
        int realmId,
        [FromBody] UpdateRealmRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var realm = await _realmService.UpdateRealmAsync(stackId, realmId, request, cancellationToken);
            return Ok(realm);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to update realm: {ex.Message}" });
        }
    }
}
