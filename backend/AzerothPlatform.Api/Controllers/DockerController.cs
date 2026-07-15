using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/docker")]
public class DockerController : ControllerBase
{
    private readonly IStackDockerService _stackDockerService;
    private readonly IDockerCleanupJobService _cleanupJobService;

    public DockerController(
        IStackDockerService stackDockerService,
        IDockerCleanupJobService cleanupJobService)
    {
        _stackDockerService = stackDockerService;
        _cleanupJobService = cleanupJobService;
    }

    [HttpGet("disk")]
    public async Task<ActionResult<DockerDiskUsageDto>> GetDiskUsage(CancellationToken cancellationToken)
        => Ok(await _stackDockerService.GetDiskUsageAsync(cancellationToken));

    [HttpPost("cleanup")]
    public ActionResult<DockerCleanupJobStatusDto> StartCleanup()
        => Ok(_cleanupJobService.Enqueue(DockerCleanupJobAction.ReclaimDiskSpace));

    [HttpPost("cleanup/old-builds")]
    public ActionResult<DockerCleanupJobStatusDto> StartOldBuildsCleanup()
        => Ok(_cleanupJobService.Enqueue(DockerCleanupJobAction.CleanupOldBuilds));

    [HttpGet("cleanup/status")]
    public ActionResult<DockerCleanupJobStatusDto?> GetCleanupStatus()
        => Ok(_cleanupJobService.GetStatus());
}
