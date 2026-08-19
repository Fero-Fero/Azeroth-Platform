using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>Manager-wide vanilla DBC CSV store used as the module extra-data trim baseline.</summary>
[Authorize]
[ApiController]
[Route("api/dbc-store")]
public sealed class DbcStoreController : ControllerBase
{
    private readonly IDbcBaselineStore _store;

    public DbcStoreController(IDbcBaselineStore store)
    {
        _store = store;
    }

    [HttpGet]
    public ActionResult<DbcBaselineStoreDto> GetStatus() => Ok(_store.GetStatus());

    /// <summary>Starts a background sync. Pass <c>force=true</c> to wipe and re-download even if the tag matches.</summary>
    [HttpPost("sync")]
    public ActionResult<DbcBaselineStoreDto> Sync([FromQuery] bool force = false) =>
        Accepted(_store.EnqueueSync(force));
}
