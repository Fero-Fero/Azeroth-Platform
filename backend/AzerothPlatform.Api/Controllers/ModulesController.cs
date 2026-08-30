using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Module catalogue endpoints.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ModulesController : ControllerBase
{
    private readonly IModuleCatalogService _moduleCatalogService;
    private readonly ICommunityModuleCatalogService _communityModuleCatalogService;
    private readonly IModuleConfigService _moduleConfigService;
    private readonly IServiceEnvTemplateService _serviceEnvTemplateService;

    public ModulesController(
        IModuleCatalogService moduleCatalogService,
        ICommunityModuleCatalogService communityModuleCatalogService,
        IModuleConfigService moduleConfigService,
        IServiceEnvTemplateService serviceEnvTemplateService)
    {
        _moduleCatalogService = moduleCatalogService;
        _communityModuleCatalogService = communityModuleCatalogService;
        _moduleConfigService = moduleConfigService;
        _serviceEnvTemplateService = serviceEnvTemplateService;
    }

    /// <summary>
    /// Per-service environment-variable templates (worldserver, authserver, armory, client) rendered
    /// by the stack wizard so env vars can be configured per container instead of one global list.
    /// </summary>
    [HttpGet("/api/service-env-templates")]
    public ActionResult<IReadOnlyList<ServiceEnvTemplate>> GetServiceEnvTemplates() =>
        Ok(_serviceEnvTemplateService.GetTemplates());

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ModuleDto>>> Get(
        [FromQuery] ServerType? serverType,
        CancellationToken cancellationToken)
    {
        var modules = await _moduleCatalogService.ListAsync(serverType, cancellationToken);
        return Ok(modules);
    }

    /// <summary>Lists every module (built-in + custom) for catalog administration.</summary>
    [HttpGet("catalog")]
    public async Task<ActionResult<IReadOnlyList<ModuleDto>>> GetCatalog(CancellationToken cancellationToken)
    {
        var modules = await _moduleCatalogService.ListAllAsync(cancellationToken);
        return Ok(modules);
    }

    /// <summary>Browses modules from the AzerothCore community catalogue (GitHub topic metadata).</summary>
    [HttpGet("community")]
    public async Task<ActionResult<CommunityModuleListResult>> GetCommunityModules(
        [FromQuery] string? search,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _communityModuleCatalogService.ListAsync(search, sort, page, pageSize, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Failed to load community module catalogue: {ex.Message}" });
        }
    }

    /// <summary>Imports a community module into the platform catalog so it can be selected on stacks.</summary>
    [HttpPost("community/import")]
    public Task<IActionResult> ImportCommunityModule(
        [FromBody] ImportCommunityModuleRequest request,
        CancellationToken cancellationToken)
        => Execute(() => _communityModuleCatalogService.ImportAsync(request.Repository, cancellationToken));

    /// <summary>Adds a custom module cloned from a git repository.</summary>
    [HttpPost]
    public Task<IActionResult> Create([FromBody] SaveModuleRequest request, CancellationToken cancellationToken)
        => Execute(() => _moduleCatalogService.CreateAsync(request, cancellationToken));

    /// <summary>Adds a custom module from an uploaded package (.zip). Multipart form with metadata + file.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(500 * 1024 * 1024)]
    public Task<IActionResult> Upload(
        [FromForm] string id,
        [FromForm] string name,
        [FromForm] string? description,
        IFormFile file,
        CancellationToken cancellationToken)
        => Execute(async () =>
        {
            if (file == null || file.Length == 0)
            {
                throw new ArgumentException("A package .zip file is required.");
            }

            var request = new SaveModuleRequest
            {
                Id = id,
                Name = name,
                Description = description ?? string.Empty
            };

            await using var stream = file.OpenReadStream();
            return await _moduleCatalogService.CreateFromPackageAsync(request, file.FileName, stream, cancellationToken);
        });

    /// <summary>Replaces the package files of an existing package module.</summary>
    [HttpPost("{moduleId}/package")]
    [RequestSizeLimit(500 * 1024 * 1024)]
    public Task<IActionResult> ReplacePackage(string moduleId, IFormFile file, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            if (file == null || file.Length == 0)
            {
                throw new ArgumentException("A package .zip file is required.");
            }

            await using var stream = file.OpenReadStream();
            return await _moduleCatalogService.ReplacePackageAsync(moduleId, file.FileName, stream, cancellationToken);
        });

    /// <summary>Gets a module's README as Markdown (from git or the uploaded package).</summary>
    [HttpGet("{moduleId}/readme")]
    public Task<IActionResult> GetReadme(string moduleId, CancellationToken cancellationToken)
        => Execute(() => _moduleCatalogService.GetReadmeAsync(moduleId, cancellationToken));

    /// <summary>Updates a custom module (built-in modules cannot be edited).</summary>
    [HttpPut("{moduleId}")]
    public Task<IActionResult> Update(string moduleId, [FromBody] SaveModuleRequest request, CancellationToken cancellationToken)
        => Execute(() => _moduleCatalogService.UpdateAsync(moduleId, request, cancellationToken));

    /// <summary>Deletes a custom module (built-in modules cannot be deleted).</summary>
    [HttpDelete("{moduleId}")]
    public Task<IActionResult> Delete(string moduleId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            await _moduleCatalogService.DeleteAsync(moduleId, cancellationToken);
            return new { success = true };
        });

    [HttpGet("{moduleId}/config")]
    public async Task<ActionResult<ModuleConfigSchema>> GetConfig(
        string moduleId,
        CancellationToken cancellationToken)
    {
        try
        {
            var schema = await _moduleConfigService.GetConfigSchemaAsync(moduleId, cancellationToken);
            return Ok(schema);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IActionResult> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            return new OkObjectResult(await action());
        }
        catch (KeyNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return new ConflictObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }
}
