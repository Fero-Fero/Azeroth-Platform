using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/express-provision")]
public sealed class ExpressProvisionController : ControllerBase
{
    private readonly IExpressProvisionService _provision;

    public ExpressProvisionController(IExpressProvisionService provision)
    {
        _provision = provision;
    }

    /// <summary>Starts Express Setup (Setup and Launch).</summary>
    [HttpPost("start")]
    public IActionResult Start(string stackId)
    {
        try
        {
            _provision.Start(stackId);
            return Accepted();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Continues Express Setup after the operator uploaded or downloaded a base client.
    /// </summary>
    [HttpPost("continue")]
    public IActionResult Continue(string stackId)
    {
        try
        {
            _provision.ContinueAfterClient(stackId);
            return Accepted();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Re-runs Express Setup from the checkpoint that failed.</summary>
    [HttpPost("retry")]
    public IActionResult Retry(string stackId)
    {
        try
        {
            _provision.Retry(stackId);
            return Accepted();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Clears the one-time "all ready, press Start" notice.</summary>
    [HttpPost("dismiss-ready")]
    public IActionResult DismissReady(string stackId)
    {
        try
        {
            _provision.DismissReadyNotice(stackId);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
