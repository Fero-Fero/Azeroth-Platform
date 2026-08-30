using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/cloud/auth")]
public class CloudAuthController : ControllerBase
{
    private readonly ICloudAuthOrchestrator _cloudAuthOrchestrator;
    private readonly CloudOAuthOptions _oauthOptions;

    public CloudAuthController(
        ICloudAuthOrchestrator cloudAuthOrchestrator,
        IOptions<CloudOAuthOptions> oauthOptions)
    {
        _cloudAuthOrchestrator = cloudAuthOrchestrator;
        _oauthOptions = oauthOptions.Value;
    }

    /// <summary>Returns per-provider sign-in capabilities (OAuth vs token vs manual-only).</summary>
    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<CloudAuthProviderStatusDto>> ListProviders()
        => Ok(_cloudAuthOrchestrator.ListProviderStatus());

    /// <summary>Starts provider sign-in. Returns an authorization URL, device code, or a manual-credentials flag.</summary>
    [HttpPost("{provider}/start")]
    public async Task<ActionResult<CloudAuthStartResultDto>> Start(
        CloudProvider provider,
        [FromBody] CloudAuthStartRequestDto? request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _cloudAuthOrchestrator.StartAsync(
                provider,
                BindStartRequest(request),
                cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// OAuth provider redirect target. Anonymous because the browser returns without the admin JWT;
    /// CSRF is enforced via the one-time <c>state</c> parameter.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{provider}/callback")]
    public async Task<IActionResult> Callback(
        CloudProvider provider,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = await _cloudAuthOrchestrator.HandleCallbackAsync(
                provider,
                code,
                state,
                error,
                errorDescription,
                cancellationToken);
            return Redirect(BuildFrontendRedirect(
                status: "success",
                provider,
                connectionId: connection.Id,
                message: null));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Redirect(BuildFrontendRedirect(
                status: "error",
                provider,
                connectionId: null,
                message: ex.Message));
        }
    }

    /// <summary>Finishes a non-redirect connect flow (AWS AssumeRole: paste Role ARN + External ID).</summary>
    [HttpPost("{provider}/complete")]
    public async Task<ActionResult<CloudProviderConnectionDto>> Complete(
        CloudProvider provider,
        [FromBody] CloudAuthCompleteRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _cloudAuthOrchestrator.CompleteAsync(
                provider,
                request ?? new CloudAuthCompleteRequestDto(),
                cancellationToken);
            return Ok(created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{provider}/refresh/{connectionId}")]
    public async Task<IActionResult> Refresh(
        CloudProvider provider,
        string connectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _cloudAuthOrchestrator.RefreshAsync(provider, connectionId, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{connectionId}/revoke")]
    public async Task<IActionResult> Revoke(string connectionId, CancellationToken cancellationToken)
    {
        try
        {
            await _cloudAuthOrchestrator.RevokeAsync(connectionId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private string BuildFrontendRedirect(
        string status,
        CloudProvider provider,
        string? connectionId,
        string? message)
    {
        var origin = string.IsNullOrWhiteSpace(_oauthOptions.FrontendBaseUrl)
            ? $"{Request.Scheme}://{Request.Host.Value}"
            : _oauthOptions.FrontendBaseUrl.TrimEnd('/');
        var path = string.IsNullOrWhiteSpace(_oauthOptions.FrontendCallbackPath)
            ? "/admin/cloud/oauth-callback"
            : _oauthOptions.FrontendCallbackPath;
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var query = new QueryString()
            .Add("status", status)
            .Add("provider", provider.ToString());
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            query = query.Add("connectionId", connectionId);
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            query = query.Add("message", message);
        }

        return origin + path + query.ToUriComponent();
    }

    private CloudAuthStartRequestDto BindStartRequest(CloudAuthStartRequestDto? request)
    {
        var payload = request ?? new CloudAuthStartRequestDto();
        if (string.IsNullOrWhiteSpace(payload.CallbackBaseUrl))
        {
            payload.CallbackBaseUrl = $"{Request.Scheme}://{Request.Host.Value}";
        }

        return payload;
    }
}
