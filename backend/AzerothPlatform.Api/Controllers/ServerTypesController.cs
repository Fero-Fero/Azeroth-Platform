using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Exposes the operator-configured server-type catalog to the stack wizard so the available variants
/// (and their descriptions/repositories) reflect configuration without a frontend change.
/// </summary>
[Authorize]
[ApiController]
[Route("api/server-types")]
public class ServerTypesController : ControllerBase
{
    private readonly IServerTypeCatalog _catalog;
    private readonly IGitService _gitService;

    public ServerTypesController(IServerTypeCatalog catalog, IGitService gitService)
    {
        _catalog = catalog;
        _gitService = gitService;
    }

    /// <summary>Lists the enabled server types selectable when creating a stack.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ServerTypeInfoDto>> Get() => Ok(_catalog.GetServerTypes());

    /// <summary>
    /// Lists the branches of a remote git repository (for the custom-fork branch picker). The URL is
    /// validated the same way as build/module repositories before it is passed to git.
    /// </summary>
    [HttpGet("branches")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetBranches(
        [FromQuery] string repositoryUrl, CancellationToken cancellationToken)
    {
        string validatedUrl;
        try
        {
            validatedUrl = ModuleCatalogService.ValidateGitRepository(repositoryUrl);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        try
        {
            var branches = await _gitService.ListRemoteBranchesAsync(validatedUrl, cancellationToken);
            return Ok(branches);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
