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

    [HttpGet("overview")]
    public async Task<ActionResult<DockerEngineOverviewDto>> GetEngineOverview(CancellationToken cancellationToken)
        => Ok(await _stackDockerService.GetEngineOverviewAsync(cancellationToken));

    [HttpDelete("volumes/{volumeName}")]
    public async Task<ActionResult<StackDockerDeleteResultDto>> DeleteEngineVolume(
        string volumeName,
        CancellationToken cancellationToken)
    {
        var result = await _stackDockerService.DeleteEngineVolumeAsync(Uri.UnescapeDataString(volumeName), cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("images/{imageId}")]
    public async Task<ActionResult<StackDockerDeleteResultDto>> DeleteEngineImage(
        string imageId,
        CancellationToken cancellationToken)
    {
        var result = await _stackDockerService.DeleteEngineImageAsync(Uri.UnescapeDataString(imageId), cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
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

    [HttpGet("manager/files")]
    public async Task<ActionResult<DockerManagerFilesDto>> GetManagerFiles(
        [FromQuery] string? path,
        CancellationToken cancellationToken)
        => Ok(await _stackDockerService.GetManagerFilesAsync(path ?? string.Empty, cancellationToken));

    [HttpDelete("manager/files")]
    public async Task<ActionResult<StackDockerDeleteResultDto>> DeleteManagerFile(
        [FromQuery] string path,
        CancellationToken cancellationToken)
    {
        var result = await _stackDockerService.DeleteManagerFileAsync(path, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("manager/cleanup-mirrors")]
    public async Task<ActionResult<DockerManagerMirrorCleanupResultDto>> CleanupManagerMirrors(
        CancellationToken cancellationToken)
        => Ok(await _stackDockerService.CleanupManagerMirrorsAsync(cancellationToken));

    [HttpPost("manager/migrate-client-mirrors")]
    public async Task<ActionResult<DockerManagerMirrorCleanupResultDto>> MigrateClientMirrors(
        CancellationToken cancellationToken)
        => Ok(await _stackDockerService.MigrateClientMirrorsToVolumesAsync(cancellationToken));

    [HttpGet("platform-keys")]
    public async Task<ActionResult<DockerPlatformKeysDto>> GetPlatformKeys(CancellationToken cancellationToken)
        => Ok(await _stackDockerService.GetPlatformKeysStatusAsync(cancellationToken));
}
