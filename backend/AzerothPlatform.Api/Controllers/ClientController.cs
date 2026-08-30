using AzerothPlatform.Api.Filters;
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
    /// Receives a base-client archive (zip, rar, 7z, tar/tar.gz, …) and queues extract + volume install
    /// as a background job so the manager stays responsive. The archive may wrap the client in a single
    /// top-level folder.
    ///
    /// The body is read as a stream rather than bound to an <c>IFormFile</c>: the base client is ~17 GB,
    /// and model binding spools the whole body to the server's temp directory before the action runs.
    /// </summary>
    [HttpPost("base")]
    [DisableFormValueModelBinding]
    [RequestSizeLimit(64L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<ClientJobStatusDto>> UploadBase(string stackId, CancellationToken cancellationToken)
    {
        if (!MultipartUpload.IsMultipartContentType(Request.ContentType))
        {
            return BadRequest(new { error = "The upload must be sent as multipart/form-data." });
        }

        // Checked before a single byte is read: rejecting afterwards would waste the whole upload.
        if (_clientJobs.GetStatus(stackId)?.IsRunning == true)
        {
            return Conflict(new { error = "A client operation is already running for this stack." });
        }

        string? stagingToken = null;
        try
        {
            var boundary = MultipartUpload.GetBoundary(Request.ContentType);
            var received = await MultipartUpload.ReadFirstFileAsync(
                Request.Body,
                boundary,
                async (_, body) =>
                    stagingToken = await _client.StageBaseClientArchiveAsync(stackId, body, cancellationToken),
                cancellationToken);

            if (!received || string.IsNullOrEmpty(stagingToken))
            {
                return BadRequest(new { error = "No file was uploaded." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        try
        {
            return Ok(_clientJobs.Enqueue(
                stackId,
                ClientJobAction.InstallBase,
                stagingArchivePath: stagingToken));
        }
        catch (InvalidOperationException ex)
        {
            await _client.DiscardStagedBaseClientArchiveAsync(stackId, stagingToken);
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Downloads a base-client archive from a URL into this stack (background job).</summary>
    [HttpPost("download")]
    public ActionResult<ClientJobStatusDto> DownloadBase(
        string stackId,
        [FromBody] DownloadBaseClientRequestDto? request)
    {
        var url = (request?.Url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "A download URL is required." });
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new { error = "The download URL must be an http or https link." });
        }

        return Ok(_clientJobs.Enqueue(stackId, ClientJobAction.DownloadBase, url));
    }

    /// <summary>
    /// Deletes every client file this stack serves — base, published patches and addons, and the cached
    /// manifest — as a background job, so a broken or half-uploaded client can be rebuilt from scratch.
    /// The built launcher, portal registry, branding and news survive.
    /// </summary>
    [HttpPost("purge")]
    public ActionResult<ClientJobStatusDto> Purge(string stackId)
    {
        if (_clientJobs.GetStatus(stackId)?.IsRunning == true)
        {
            return Conflict(new { error = "A client operation is already running for this stack." });
        }

        return Ok(_clientJobs.Enqueue(stackId, ClientJobAction.PurgeContent));
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
    /// Uploads a single file into a folder of the stack's client (drag &amp; drop in the file browser).
    /// <paramref name="path"/> is the destination folder relative to the client root ('' = root);
    /// escaping it is rejected. The file lands in the base or overlay layer depending on whether the
    /// path is platform-managed. Returns the updated base info.
    ///
    /// Streamed rather than model-bound, so dropping a multi-GB MPQ does not spool through the server's
    /// temp directory first.
    /// </summary>
    [HttpPost("file")]
    [DisableFormValueModelBinding]
    [RequestSizeLimit(8L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<ClientBaseInfoDto>> UploadFile(
        string stackId, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        if (!MultipartUpload.IsMultipartContentType(Request.ContentType))
        {
            return BadRequest(new { error = "The upload must be sent as multipart/form-data." });
        }

        try
        {
            ClientBaseInfoDto? info = null;
            var boundary = MultipartUpload.GetBoundary(Request.ContentType);
            var received = await MultipartUpload.ReadFirstFileAsync(
                Request.Body,
                boundary,
                async (fileName, body) =>
                    info = await _client.UploadFileAsync(
                        stackId, path ?? string.Empty, fileName, body, cancellationToken),
                cancellationToken);

            if (!received || info is null)
            {
                return BadRequest(new { error = "No file was uploaded." });
            }

            return Ok(info);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
