using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Character management endpoints for AzerothCore stacks
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/characters")]
public class CharactersController : ControllerBase
{
    private readonly IAccountManagementService _accountService;

    public CharactersController(IAccountManagementService accountService)
    {
        _accountService = accountService;
    }

    /// <summary>
    /// Get all characters across all accounts for a stack
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<CharacterDto>>> GetAll(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var characters = await _accountService.GetAllCharactersAsync(stackId, cancellationToken);
            return Ok(characters);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to retrieve characters: {ex.Message}" });
        }
    }

    /// <summary>
    /// Create the dedicated AH Bot account and inject Alliance + Horde bot characters directly into the database.
    /// Idempotent - safe to call multiple times.
    /// </summary>
    [HttpPost("ahbot-account")]
    public async Task<ActionResult<AhBotSetupResultDto>> CreateAhBotAccount(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _accountService.CreateAhBotCharactersAsync(stackId, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to create AH Bot account: {ex.Message}" });
        }
    }

    /// <summary>
    /// Send a message to a character
    /// </summary>
    [HttpPost("{characterName}/send-message")]
    public async Task<IActionResult> SendMessage(
        string stackId,
        string characterName,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { error = "Message is required" });
        }

        try
        {
            var success = await _accountService.SendMessageAsync(
                stackId, characterName, request.Message, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Message sent to '{characterName}'" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to send message. Character may not be online or doesn't exist." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to send message: {ex.Message}" });
        }
    }

    /// <summary>
    /// Send items to a character via in-game mail
    /// </summary>
    [HttpPost("{characterName}/send-items")]
    public async Task<IActionResult> SendItems(
        string stackId,
        string characterName,
        [FromBody] SendItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ItemId <= 0)
        {
            return BadRequest(new { error = "Valid item ID is required" });
        }

        if (request.Count <= 0)
        {
            return BadRequest(new { error = "Item count must be greater than 0" });
        }

        try
        {
            var success = await _accountService.SendItemsAsync(
                stackId, characterName, request.ItemId, request.Count, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Items sent to '{characterName}' via mail" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to send items. Character may not exist or item ID is invalid." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to send items: {ex.Message}" });
        }
    }

    /// <summary>
    /// Send money to a character via in-game mail
    /// </summary>
    [HttpPost("{characterName}/send-money")]
    public async Task<IActionResult> SendMoney(
        string stackId,
        string characterName,
        [FromBody] SendMoneyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.CopperAmount <= 0)
        {
            return BadRequest(new { error = "Amount must be greater than 0" });
        }

        try
        {
            var success = await _accountService.SendMoneyAsync(
                stackId, characterName, request.CopperAmount, cancellationToken);

            if (success)
            {
                var gold = request.CopperAmount / 10000;
                var silver = (request.CopperAmount % 10000) / 100;
                var copper = request.CopperAmount % 100;
                
                return Ok(new 
                { 
                    success = true, 
                    message = $"Money sent to '{characterName}' via mail: {gold}g {silver}s {copper}c" 
                });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to send money. Character may not exist." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to send money: {ex.Message}" });
        }
    }

    /// <summary>
    /// Kick a player from the server
    /// </summary>
    [HttpPost("{characterName}/kick")]
    public async Task<IActionResult> KickPlayer(
        string stackId,
        string characterName,
        [FromBody] KickPlayerRequest? request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var reason = request?.Reason ?? string.Empty;
            var success = await _accountService.KickPlayerAsync(
                stackId, characterName, reason, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Player '{characterName}' has been kicked" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to kick player. Character may not be online." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to kick player: {ex.Message}" });
        }
    }

    /// <summary>
    /// Force a character to be renamed on next login
    /// </summary>
    [HttpPost("{characterName}/rename")]
    public async Task<IActionResult> RenameCharacter(
        string stackId,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await _accountService.RenameCharacterAsync(
                stackId, characterName, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Character '{characterName}' will be renamed on next login" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to mark character for rename. Character may not exist." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to rename character: {ex.Message}" });
        }
    }

    /// <summary>
    /// Force a character to be customized on next login
    /// </summary>
    [HttpPost("{characterName}/customize")]
    public async Task<IActionResult> CustomizeCharacter(
        string stackId,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await _accountService.CustomizeCharacterAsync(
                stackId, characterName, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Character '{characterName}' will be customized on next login" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to mark character for customization. Character may not exist." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to customize character: {ex.Message}" });
        }
    }

    /// <summary>
    /// Set character level
    /// </summary>
    [HttpPost("{characterName}/set-level")]
    public async Task<IActionResult> SetCharacterLevel(
        string stackId,
        string characterName,
        [FromBody] SetCharacterLevelRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Level < 1 || request.Level > 80)
        {
            return BadRequest(new { error = "Level must be between 1 and 80" });
        }

        try
        {
            var success = await _accountService.SetCharacterLevelAsync(
                stackId, characterName, request.Level, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Character '{characterName}' level set to {request.Level}" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to set character level. Character may not exist." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to set character level: {ex.Message}" });
        }
    }

    /// <summary>
    /// Get the full inventory (equipment, bags, backpack, bank) for a character by GUID
    /// </summary>
    [HttpGet("{characterGuid:int}/inventory")]
    public async Task<ActionResult<CharacterInventoryDto>> GetInventory(
        string stackId,
        int characterGuid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var inventory = await _accountService.GetCharacterInventoryAsync(stackId, characterGuid, cancellationToken);
            return Ok(inventory);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to retrieve inventory: {ex.Message}" });
        }
    }

    /// <summary>
    /// Ban a character
    /// </summary>
    [HttpPost("{characterName}/ban")]
    public async Task<IActionResult> BanCharacter(
        string stackId,
        string characterName,
        [FromBody] BanCharacterRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Duration))
            return BadRequest(new { error = "Duration is required (e.g. '30m', '7d', '-1' for permanent)" });
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = "Reason is required" });

        try
        {
            var success = await _accountService.BanCharacterAsync(stackId, characterName, request.Duration, request.Reason, cancellationToken);
            return success
                ? Ok(new { success = true, message = $"Character '{characterName}' banned for {request.Duration}" })
                : BadRequest(new { success = false, error = "Failed to ban character. Character may not exist." });
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Failed to ban character: {ex.Message}" }); }
    }

    /// <summary>
    /// Unban a character
    /// </summary>
    [HttpDelete("{characterName}/ban")]
    public async Task<IActionResult> UnbanCharacter(
        string stackId,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await _accountService.UnbanCharacterAsync(stackId, characterName, cancellationToken);
            return success
                ? Ok(new { success = true, message = $"Character '{characterName}' unbanned" })
                : BadRequest(new { success = false, error = "Failed to unban character." });
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Failed to unban character: {ex.Message}" }); }
    }

    /// <summary>
    /// Mute a character's chat
    /// </summary>
    [HttpPost("{characterName}/mute")]
    public async Task<IActionResult> MuteCharacter(
        string stackId,
        string characterName,
        [FromBody] MuteCharacterRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Minutes < 1)
            return BadRequest(new { error = "Minutes must be at least 1" });
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = "Reason is required" });

        try
        {
            var success = await _accountService.MuteCharacterAsync(stackId, characterName, request.Minutes, request.Reason, cancellationToken);
            return success
                ? Ok(new { success = true, message = $"Character '{characterName}' muted for {request.Minutes} minutes" })
                : BadRequest(new { success = false, error = "Failed to mute character." });
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Failed to mute character: {ex.Message}" }); }
    }

    /// <summary>
    /// Unmute a character's chat
    /// </summary>
    [HttpPost("{characterName}/unmute")]
    public async Task<IActionResult> UnmuteCharacter(
        string stackId,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await _accountService.UnmuteCharacterAsync(stackId, characterName, cancellationToken);
            return success
                ? Ok(new { success = true, message = $"Character '{characterName}' unmuted" })
                : BadRequest(new { success = false, error = "Failed to unmute character." });
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Failed to unmute character: {ex.Message}" }); }
    }

    /// <summary>
    /// Revive a dead character
    /// </summary>
    [HttpPost("{characterName}/revive")]
    public async Task<IActionResult> ReviveCharacter(
        string stackId,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await _accountService.ReviveCharacterAsync(stackId, characterName, cancellationToken);
            return success
                ? Ok(new { success = true, message = $"Character '{characterName}' revived" })
                : BadRequest(new { success = false, error = "Failed to revive character." });
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Failed to revive character: {ex.Message}" }); }
    }

    /// <summary>
    /// Modify gold for a character (send via mail, positive amounts only)
    /// </summary>
    [HttpPost("{characterName}/modify-money")]
    public async Task<IActionResult> ModifyMoney(
        string stackId,
        string characterName,
        [FromBody] ModifyMoneyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.CopperAmount == 0)
            return BadRequest(new { error = "CopperAmount must be non-zero (positive to add, negative to remove)" });

        try
        {
            var success = await _accountService.ModifyMoneyAsync(stackId, characterName, request.CopperAmount, cancellationToken);
            return success
                ? Ok(new { success = true, message = $"Money modified for '{characterName}'" })
                : BadRequest(new { success = false, error = "Failed to modify money." });
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Failed to modify money: {ex.Message}" }); }
    }

    /// <summary>
    /// Add an item directly to a character's inventory
    /// </summary>
    [HttpPost("{characterName}/add-item")]
    public async Task<IActionResult> AddItem(
        string stackId,
        string characterName,
        [FromBody] AddItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ItemId <= 0)
            return BadRequest(new { error = "ItemId must be a positive item entry ID" });
        if (request.Count < 1)
            return BadRequest(new { error = "Count must be at least 1" });

        try
        {
            var success = await _accountService.AddItemAsync(stackId, characterName, request.ItemId, request.Count, cancellationToken);
            return success
                ? Ok(new { success = true, message = $"Added item {request.ItemId} x{request.Count} to '{characterName}'" })
                : BadRequest(new { success = false, error = "Failed to add item. Item entry may be invalid." });
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Failed to add item: {ex.Message}" }); }
    }
}

