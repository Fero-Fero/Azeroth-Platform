using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>Per-stack module extra-data: choose → prepare InstalledModules → deposit after SOAP.</summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/module-extra-data")]
public sealed class ModuleExtraDataController : ControllerBase
{
    private readonly IModuleInstallOrchestrator _orchestrator;
    private readonly IModuleInstallJobService _jobs;

    public ModuleExtraDataController(
        IModuleInstallOrchestrator orchestrator,
        IModuleInstallJobService jobs)
    {
        _orchestrator = orchestrator;
        _jobs = jobs;
    }

    [HttpGet("choices")]
    public async Task<ActionResult<StackModuleInstallChoicesDto>> GetChoices(
        string stackId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _orchestrator.DescribeChoicesAsync(stackId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("choices")]
    public async Task<ActionResult<ModuleExtraDataStackStatusDto>> SaveChoices(
        string stackId, [FromBody] ApplyModuleExtraDataRequest? request, CancellationToken cancellationToken)
    {
        try
        {
            await _orchestrator.SaveChoicesAsync(stackId, request ?? new ApplyModuleExtraDataRequest(), cancellationToken);
            return Ok(_orchestrator.GetStackStatus(stackId));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("stack-status")]
    public ActionResult<ModuleExtraDataStackStatusDto> GetStackStatus(string stackId)
    {
        try
        {
            return Ok(_orchestrator.GetStackStatus(stackId));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("status")]
    public ActionResult<ModuleInstallJobStatusDto?> GetStatus(string stackId) =>
        Ok(_jobs.GetStatus(stackId));

    [HttpPost("prepare")]
    public ActionResult<ModuleInstallJobStatusDto> Prepare(
        string stackId, [FromBody] ApplyModuleExtraDataRequest? request)
    {
        return Accepted(_jobs.EnqueuePrepare(stackId, request ?? new ApplyModuleExtraDataRequest()));
    }

    [HttpPost("deposit")]
    public ActionResult<ModuleInstallJobStatusDto> Deposit(string stackId) =>
        Accepted(_jobs.EnqueueDeposit(stackId));

    [HttpPost("apply")]
    public ActionResult<ModuleInstallJobStatusDto> Apply(
        string stackId, [FromBody] ApplyModuleExtraDataRequest? request)
    {
        return Accepted(_jobs.Enqueue(stackId, request ?? new ApplyModuleExtraDataRequest()));
    }
}
