using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Manages addons served through a <b>specific stack's</b> client
/// (<c>api/stacks/{stackId}/addons</c>).
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/addons")]
public class StackAddonsController : ControllerBase
{
    private readonly IAddonService _addons;

    public StackAddonsController(IAddonService addons)
    {
        _addons = addons;
    }

    /// <summary>Lists the addons currently served for this stack's client.</summary>
    [HttpGet]
    public Task<IActionResult> List(string stackId, CancellationToken cancellationToken)
        => AddonApi.Execute(() => _addons.ListAsync(stackId, cancellationToken));

    /// <summary>Uploads a .zip archive of one or more addons and rescans this stack's manifest.</summary>
    [HttpPost]
    [RequestSizeLimit(AddonApi.MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = AddonApi.MaxUploadBytes)]
    public Task<IActionResult> Upload(string stackId, [FromForm] IFormFile? file, CancellationToken cancellationToken)
        => AddonApi.Upload(_addons, stackId, file, cancellationToken);

    /// <summary>Deletes a served addon by folder name and rescans this stack's manifest.</summary>
    [HttpDelete("{addonName}")]
    public Task<IActionResult> Delete(string stackId, string addonName, CancellationToken cancellationToken)
        => AddonApi.Execute(() => _addons.DeleteAsync(stackId, addonName, cancellationToken));

    /// <summary>The static addon catalog with install status for this stack's client.</summary>
    [HttpGet("catalog")]
    public Task<IActionResult> Catalog(string stackId, CancellationToken cancellationToken)
        => AddonApi.Execute(() => _addons.GetCatalogAsync(stackId, cancellationToken));

    /// <summary>Installs a catalog addon into this stack's client and rescans the manifest.</summary>
    [HttpPost("catalog/{addonId}/install")]
    public Task<IActionResult> Install(string stackId, string addonId, CancellationToken cancellationToken)
        => AddonApi.Execute(() => _addons.InstallFromCatalogAsync(stackId, addonId, cancellationToken));
}

/// <summary>Shared request handling for the addon controllers (error mapping + upload plumbing).</summary>
internal static class AddonApi
{
    /// <summary>
    /// Maximum accepted addon upload size. Some addons (e.g. full storyline/voice-over packs) ship
    /// several gigabytes of data, so this is set well above typical sizes. Applied to BOTH the request
    /// body limit (<see cref="RequestSizeLimitAttribute"/>) and the multipart section limit
    /// (<see cref="RequestFormLimitsAttribute"/>) - without the latter, form binding rejects anything
    /// over the ~128 MB default long before the service runs.
    /// </summary>
    public const long MaxUploadBytes = 16L * 1024 * 1024 * 1024;

    public static Task<IActionResult> Upload(
        IAddonService addons, string? stackId, IFormFile? file, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            if (file is null || file.Length == 0)
            {
                throw new ArgumentException("No file was uploaded.");
            }

            await using var stream = file.OpenReadStream();
            return await addons.UploadZipAsync(stackId, file.FileName, stream, cancellationToken);
        });

    public static async Task<IActionResult> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            return new OkObjectResult(await action());
        }
        catch (KeyNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }
}
