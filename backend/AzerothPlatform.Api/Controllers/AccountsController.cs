using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Account management endpoints for AzerothCore stacks
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/accounts")]
public class AccountsController : ControllerBase
{
    private readonly IAccountManagementService _accountService;

    public AccountsController(IAccountManagementService accountService)
    {
        _accountService = accountService;
    }

    /// <summary>
    /// Get all accounts for a stack
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<AccountDto>>> GetAccounts(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var accounts = await _accountService.GetAccountsAsync(stackId, cancellationToken);
            return Ok(accounts);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { 
                error = $"Failed to retrieve accounts: {ex.Message}",
                details = ex.InnerException?.Message,
                type = ex.GetType().Name,
                stackTrace = ex.StackTrace
            });
        }
    }

    /// <summary>
    /// Get all characters for a specific account
    /// </summary>
    [HttpGet("{accountId}/characters")]
    public async Task<ActionResult<List<CharacterDto>>> GetCharacters(
        string stackId,
        int accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var characters = await _accountService.GetCharactersAsync(stackId, accountId, cancellationToken);
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
    /// Create a new account
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateAccount(
        string stackId,
        [FromBody] CreateAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(new { error = "Username is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Password is required" });
        }

        try
        {
            var (success, message) = await _accountService.CreateAccountAsync(
                stackId, request.Username, request.Password, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Account '{request.Username}' created successfully" });
            }
            else
            {
                return BadRequest(new { success = false, error = message ?? "Failed to create account. Check server logs for details." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to create account: {ex.Message}" });
        }
    }

    /// <summary>
    /// Set GM level for an account
    /// </summary>
    [HttpPost("{accountId}/set-gm-level")]
    public async Task<IActionResult> SetGmLevel(
        string stackId,
        int accountId,
        [FromBody] SetGmLevelRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(new { error = "Username is required" });
        }

        if (request.Level < 0 || request.Level > 3)
        {
            return BadRequest(new { error = "GM level must be between 0 and 3" });
        }

        try
        {
            var success = await _accountService.SetGmLevelAsync(
                stackId, request.Username, request.Level, request.RealmId, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"GM level set to {request.Level} for account '{request.Username}'" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to set GM level. Check server logs for details." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to set GM level: {ex.Message}" });
        }
    }

    /// <summary>
    /// Ban an account
    /// </summary>
    [HttpPost("{accountId}/ban")]
    public async Task<IActionResult> BanAccount(
        string stackId,
        int accountId,
        [FromBody] BanAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(new { error = "Username is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Duration))
        {
            return BadRequest(new { error = "Duration is required (e.g., '30m', '1h', 'permanent')" });
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "Reason is required" });
        }

        try
        {
            var success = await _accountService.BanAccountAsync(
                stackId, request.Username, request.Duration, request.Reason, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Account '{request.Username}' banned for {request.Duration}" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to ban account. Check server logs for details." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to ban account: {ex.Message}" });
        }
    }

    /// <summary>
    /// Delete an account
    /// </summary>
    [HttpDelete("{accountId}")]
    public async Task<IActionResult> DeleteAccount(
        string stackId,
        int accountId,
        [FromBody] DeleteAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(new { error = "Username is required" });
        }

        try
        {
            var success = await _accountService.DeleteAccountAsync(
                stackId, request.Username, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Account '{request.Username}' deleted successfully" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to delete account. Check server logs for details." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to delete account: {ex.Message}" });
        }
    }

    /// <summary>
    /// Reset account password
    /// </summary>
    [HttpPost("{accountId}/set-password")]
    public async Task<IActionResult> SetPassword(
        string stackId,
        int accountId,
        [FromBody] SetPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(new { error = "Username is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Password is required" });
        }

        try
        {
            var success = await _accountService.SetPasswordAsync(
                stackId, request.Username, request.Password, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Password changed for account '{request.Username}'" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to change password. Check server logs for details." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to change password: {ex.Message}" });
        }
    }

    /// <summary>
    /// Unban an account
    /// </summary>
    [HttpPost("{accountId}/unban")]
    public async Task<IActionResult> UnbanAccount(
        string stackId,
        int accountId,
        [FromBody] UnbanAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(new { error = "Username is required" });
        }

        try
        {
            var success = await _accountService.UnbanAccountAsync(
                stackId, request.Username, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"Account '{request.Username}' unbanned successfully" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to unban account. Check server logs for details." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to unban account: {ex.Message}" });
        }
    }

    /// <summary>
    /// Unban an IP address
    /// </summary>
    [HttpPost("unban-ip")]
    public async Task<IActionResult> UnbanIp(
        string stackId,
        [FromBody] UnbanIpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Ip))
        {
            return BadRequest(new { error = "IP address is required" });
        }

        try
        {
            var success = await _accountService.UnbanIpAsync(
                stackId, request.Ip, cancellationToken);

            if (success)
            {
                return Ok(new { success = true, message = $"IP '{request.Ip}' unbanned successfully" });
            }
            else
            {
                return BadRequest(new { success = false, error = "Failed to unban IP. Check server logs for details." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to unban IP: {ex.Message}" });
        }
    }
}
