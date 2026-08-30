using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Global addon catalog (not stack-scoped). Stack install/list stays on
/// <see cref="StackAddonsController"/>.
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

    /// <summary>Every built-in catalog entry, for wizard notices and id lookups.</summary>
    [HttpGet("catalog")]
    public ActionResult<IReadOnlyList<AddonCatalogEntryDto>> Catalog()
        => Ok(_addons.GetCatalogDefinitions());
}
