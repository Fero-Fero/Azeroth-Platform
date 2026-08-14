using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Per-stack management of the armory asset bundles used by a stack's armory: the 3D model-viewer
/// dataset (armory.data.zip + armory.textures.zip) and the static web assets (armory.static.zip). Each
/// stack has its own bundles, so armory data is uploaded per stack.
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/armory-assets")]
public class ArmoryAssetsController : ControllerBase
{
    private readonly IArmoryAssetsService _assets;
    private readonly IArmoryJobService _armoryJobs;

    public ArmoryAssetsController(IArmoryAssetsService assets, IArmoryJobService armoryJobs)
    {
        _assets = assets;
        _armoryJobs = armoryJobs;
    }

    /// <summary>Returns a summary of the stack's uploaded armory asset bundles.</summary>
    [HttpGet]
    public async Task<ActionResult<ArmoryAssetsInfoDto>> GetInfo(string stackId, CancellationToken cancellationToken)
        => Ok(await _assets.GetInfoAsync(stackId, cancellationToken));

    /// <summary>Returns the default palette for each styling template (Classic, TBC, WotLK, Custom).</summary>
    [HttpGet("styling/defaults")]
    public ActionResult<Dictionary<string, ArmoryStylingDto>> GetStylingDefaults()
        => Ok(_assets.GetStylingDefaults());

    /// <summary>Returns the stack's armory styling settings.</summary>
    [HttpGet("styling")]
    public async Task<ActionResult<ArmoryStylingDto>> GetStyling(string stackId, CancellationToken cancellationToken)
        => Ok(await _assets.GetStylingAsync(stackId, cancellationToken));

    /// <summary>Saves the stack's armory styling settings and marks the armory image for rebuild.</summary>
    [HttpPut("styling")]
    public async Task<ActionResult<ArmoryStylingDto>> SaveStyling(
        string stackId, [FromBody] ArmoryStylingDto styling, CancellationToken cancellationToken)
        => Ok(await _assets.SaveStylingAsync(stackId, styling, cancellationToken));

    /// <summary>Returns the stack's uploaded custom wallpaper image, if any.</summary>
    [HttpGet("styling/wallpaper")]
    public ActionResult GetWallpaper(string stackId)
    {
        var resolved = _assets.TryGetWallpaperFile(stackId);
        return resolved is null
            ? NotFound()
            : PhysicalFile(resolved.Value.Path, resolved.Value.ContentType);
    }

    /// <summary>Returns the resolved widget layout for a page + template combination.</summary>
    [HttpGet("layout/template")]
    public ActionResult<ArmoryPageLayoutDto> GetPageTemplate(
        [FromQuery] string pageId, [FromQuery] string templateId)
        => Ok(_assets.GetPageTemplate(pageId, templateId));

    /// <summary>Returns the stack's armory homepage layout settings.</summary>
    [HttpGet("layout")]
    public async Task<ActionResult<ArmoryLayoutDto>> GetLayout(string stackId, CancellationToken cancellationToken)
        => Ok(await _assets.GetLayoutAsync(stackId, cancellationToken));

    /// <summary>Saves the stack's armory homepage layout and marks the armory image for rebuild.</summary>
    [HttpPut("layout")]
    public async Task<ActionResult<ArmoryLayoutDto>> SaveLayout(
        string stackId, [FromBody] ArmoryLayoutDto layout, CancellationToken cancellationToken)
        => Ok(await _assets.SaveLayoutAsync(stackId, layout, cancellationToken));

    /// <summary>Uploads a wallpaper for the stack's generated armory theme and marks the armory image for rebuild.</summary>
    [HttpPost("styling/wallpaper")]
    [RequestSizeLimit(64L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 64L * 1024 * 1024)]
    public async Task<ActionResult<ArmoryStylingDto>> UploadWallpaper(
        string stackId, [FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _assets.UploadWallpaperAsync(stackId, file.FileName, stream, file.ContentType, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Returns the stack's uploaded armory favicon, if any.</summary>
    [HttpGet("favicon")]
    public ActionResult GetFavicon(string stackId)
    {
        var resolved = _assets.TryGetFaviconFile(stackId);
        return resolved is null
            ? NotFound()
            : PhysicalFile(resolved.Value.Path, resolved.Value.ContentType);
    }

    /// <summary>Uploads a favicon for the stack's armory site and marks the armory image for rebuild.</summary>
    [HttpPost("favicon")]
    [RequestSizeLimit(2L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2L * 1024 * 1024)]
    public async Task<ActionResult<ArmoryAssetsInfoDto>> UploadFavicon(
        string stackId, [FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _assets.UploadFaviconAsync(stackId, file.FileName, stream, file.ContentType, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Removes the stack's uploaded armory favicon and marks the armory image for rebuild.</summary>
    [HttpDelete("favicon")]
    public async Task<ActionResult<ArmoryAssetsInfoDto>> DeleteFavicon(string stackId, CancellationToken cancellationToken)
        => Ok(await _assets.DeleteFaviconAsync(stackId, cancellationToken));

    /// <summary>
    /// Lists one directory level of the stack's uploaded model-viewer dataset so it can be navigated in
    /// the file browser. <paramref name="path"/> is relative to the dataset root ('' = root); traversal
    /// outside the dataset is rejected (returns an empty, non-existent listing).
    /// </summary>
    [HttpGet("data/browse")]
    public async Task<ActionResult<ClientBrowseResultDto>> BrowseData(string stackId, [FromQuery] string? path, CancellationToken cancellationToken)
        => Ok(await _assets.BrowseDataAsync(stackId, path ?? string.Empty, cancellationToken));

    /// <summary>
    /// Deletes a file or folder from the stack's uploaded model-viewer dataset. <paramref name="path"/>
    /// is relative to the dataset root; deleting the root or escaping it is rejected.
    /// </summary>
    [HttpDelete("data/entry")]
    public async Task<ActionResult<ArmoryAssetsInfoDto>> DeleteData(string stackId, [FromQuery] string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "A path is required." });
        }

        try
        {
            return Ok(await _assets.DeleteDataAsync(stackId, path, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Uploads a single file into a folder of the stack's model-viewer dataset (drag &amp; drop in the
    /// data file browser). <paramref name="path"/> is the destination folder relative to the dataset root
    /// ('' = root); escaping it is rejected. Returns the updated asset info.
    /// </summary>
    [HttpPost("data/file")]
    [RequestSizeLimit(8L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 8L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<ArmoryAssetsInfoDto>> UploadDataFile(
        string stackId, [FromForm] IFormFile file, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _assets.UploadDataFileAsync(stackId, path ?? string.Empty, file.FileName, stream, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Uploads a model-viewer bundle (armory.data.zip or armory.textures.zip; any archive format,
    /// streamed as it can be multiple GB). Merges into the stack's dataset and refreshes its assets
    /// volume so a running stack picks it up.
    /// </summary>
    [HttpPost("data")]
    [RequestSizeLimit(32L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 32L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<ArmoryAssetsInfoDto>> UploadData(string stackId, [FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _assets.UploadDataAsync(stackId, stream, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Uploads the static web-asset bundle (armory.static.zip; any archive format). Merges into the
    /// stack's static directory and flags its armory image for rebuild.
    /// </summary>
    [HttpPost("static")]
    [RequestSizeLimit(8L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 8L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<ArmoryAssetsInfoDto>> UploadStatic(string stackId, [FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _assets.UploadStaticAsync(stackId, stream, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Downloads <c>armory.data.zip</c>, <c>armory.textures.zip</c>, and <c>armory.static.zip</c> from the
    /// configured GitHub release and applies them to the stack (same outcome as manual uploads).
    /// </summary>
    [HttpPost("download-release")]
    public async Task<ActionResult<ArmoryReleaseDownloadResultDto>> DownloadRelease(
        string stackId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _assets.DownloadLatestReleaseAssetsAsync(stackId, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes uploaded static web assets, preserving model-viewer data and generated styling assets.
    /// </summary>
    [HttpDelete("static")]
    public async Task<ActionResult<ArmoryAssetsInfoDto>> DeleteStatic(string stackId, CancellationToken cancellationToken)
        => Ok(await _assets.DeleteStaticAsync(stackId, cancellationToken));

    /// <summary>
    /// Rebuilds the stack's armory image (baking uploaded static assets) and restarts its armory as a
    /// detached background job, so the operation survives the request that triggered it (e.g. a page
    /// refresh). The job clears the "rebuild pending" flag on success. Returns the initial job status;
    /// the UI reattaches via the armory job status endpoint / SignalR stream.
    /// </summary>
    [HttpPost("rebuild-image")]
    public ActionResult<ArmoryJobStatusDto> RebuildImage(string stackId)
        => Ok(_armoryJobs.Enqueue(stackId, ArmoryJobAction.Rebuild));

    /// <summary>
    /// Extracts the stack's live server DBCs, converts the armory's required tables to CSV, bakes them
    /// into the armory image and restarts it — all as a detached background job (survives a page
    /// refresh). Requires the stack to have started at least once so its client data is populated.
    /// </summary>
    [HttpPost("sync-dbcs")]
    public ActionResult<ArmoryJobStatusDto> SyncDbcs(string stackId)
        => Ok(_armoryJobs.Enqueue(stackId, ArmoryJobAction.SyncDbc));
}
