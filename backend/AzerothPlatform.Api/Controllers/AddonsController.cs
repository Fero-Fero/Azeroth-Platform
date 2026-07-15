using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Manages addons served through the <b>global</b> client (<c>api/addons</c>). Addons are stored
/// under the client root's <c>game/Interface/AddOns/</c> and distributed via the launcher manifest.
/// </summary>
[Authorize]
[ApiController]
[Route("api/addons")]
public class AddonsController : ControllerBase
{
    private readonly IAddonService _addons;

    public AddonsController(IAddonService addons)
    {
        _addons = addons;
    }

    /// <summary>Lists the addons currently served for the global client.</summary>
    [HttpGet]
    public Task<IActionResult> List(CancellationToken cancellationToken)
        => AddonApi.Execute(() => _addons.ListAsync(null, cancellationToken));

    /// <summary>Uploads a .zip archive of one or more addons and rescans the manifest.</summary>
    [HttpPost]
    [RequestSizeLimit(AddonApi.MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = AddonApi.MaxUploadBytes)]
    public Task<IActionResult> Upload([FromForm] IFormFile? file, CancellationToken cancellationToken)
        => AddonApi.Upload(_addons, null, file, cancellationToken);

    /// <summary>Deletes a served addon by folder name and rescans the manifest.</summary>
    [HttpDelete("{addonName}")]
    public Task<IActionResult> Delete(string addonName, CancellationToken cancellationToken)
        => AddonApi.Execute(() => _addons.DeleteAsync(null, addonName, cancellationToken));

    /// <summary>The static addon catalog with install status for the global client.</summary>
    [HttpGet("catalog")]
    public Task<IActionResult> Catalog(CancellationToken cancellationToken)
        => AddonApi.Execute(() => _addons.GetCatalogAsync(null, cancellationToken));

    /// <summary>Installs a catalog addon into the global client and rescans the manifest.</summary>
    [HttpPost("catalog/{addonId}/install")]
    public Task<IActionResult> Install(string addonId, CancellationToken cancellationToken)
        => AddonApi.Execute(() => _addons.InstallFromCatalogAsync(null, addonId, cancellationToken));
}
