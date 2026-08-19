using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Per-stack management of the BASE WoW client that a stack's client container serves as its read-only
/// base layer: upload a base client archive, inspect it, and re-seed it. Each stack has its own base,
/// so the client is uploaded per stack.
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/client")]
public class ClientController : ControllerBase
{
    private readonly IClientService _client;
    private readonly IClientJobService _clientJobs;

    public ClientController(IClientService client, IClientJobService clientJobs)
    {
        _client = client;
        _clientJobs = clientJobs;
    }

    /// <summary>Returns a summary of the stack's currently uploaded base client.</summary>
    [HttpGet]
    public async Task<ActionResult<ClientBaseInfoDto>> GetBaseInfo(string stackId, CancellationToken cancellationToken)
        => Ok(await _client.GetBaseInfoAsync(stackId, cancellationToken));

    /// <summary>
    /// Uploads a base-client archive (zip, rar, 7z, tar/tar.gz, …; streamed, the base client is ~17 GB)
    /// and installs it as this stack's base layer, then re-seeds its base volume. The archive may wrap
    /// the client in a single top-level folder.
    /// </summary>
    [HttpPost("base")]
    [RequestSizeLimit(64L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 64L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<ClientBaseInfoDto>> UploadBase(string stackId, [FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var info = await _client.UploadBaseClientAsync(stackId, stream, cancellationToken);
            return Ok(info);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Downloads the configured base-client archive into this stack (background job).</summary>
    [HttpPost("download")]
    public async Task<ActionResult<ClientJobStatusDto>> DownloadBase(string stackId, CancellationToken cancellationToken)
    {
        var info = await _client.GetBaseInfoAsync(stackId, cancellationToken);
        if (!info.DownloadAvailable)
        {
            return BadRequest(new { error = info.DownloadUnavailableReason ?? "Download is not configured." });
        }

        return Ok(_clientJobs.Enqueue(stackId, ClientJobAction.DownloadBase));
    }

    /// <summary>Re-seeds the stack's base client volume from its base directory.</summary>
    [HttpPost("rescan")]
    public async Task<ActionResult<ClientBaseInfoDto>> Rescan(string stackId, CancellationToken cancellationToken)
        => Ok(await _client.RescanBaseAsync(stackId, cancellationToken));

    /// <summary>
    /// Lists one directory level of the stack's base client so it can be navigated in the admin file
    /// browser. <paramref name="path"/> is relative to the base root ('' = root); traversal outside the
    /// base is rejected (returns an empty, non-existent listing).
    /// </summary>
    [HttpGet("browse")]
    public async Task<ActionResult<ClientBrowseResultDto>> Browse(string stackId, [FromQuery] string? path, CancellationToken cancellationToken)
        => Ok(await _client.BrowseAsync(stackId, path ?? string.Empty, cancellationToken));

    /// <summary>
    /// Deletes a file or folder from the stack's base client. <paramref name="path"/> is relative to the
    /// base root; deleting the root or escaping it is rejected. Returns the updated base info.
    /// </summary>
    [HttpDelete("entry")]
    public async Task<ActionResult<ClientBaseInfoDto>> DeleteEntry(string stackId, [FromQuery] string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "A path is required." });
        }

        try
        {
            return Ok(await _client.DeleteEntryAsync(stackId, path, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Uploads a single file into a folder of the stack's base client (drag &amp; drop in the file
    /// browser). <paramref name="path"/> is the destination folder relative to the base root ('' = root);
    /// escaping it is rejected. Returns the updated base info.
    /// </summary>
    [HttpPost("file")]
    [RequestSizeLimit(8L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 8L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<ClientBaseInfoDto>> UploadFile(
        string stackId, [FromForm] IFormFile file, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _client.UploadFileAsync(stackId, path ?? string.Empty, file.FileName, stream, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
