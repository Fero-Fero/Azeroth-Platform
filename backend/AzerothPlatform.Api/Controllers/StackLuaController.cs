using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Manages a stack's Lua scripts (served to the worldserver's Eluna engine) under
/// <c>api/stacks/{stackId}/lua</c>.
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/lua")]
public class StackLuaController : ControllerBase
{
    private readonly ILuaScriptService _lua;
    private readonly IStackService _stacks;

    public StackLuaController(ILuaScriptService lua, IStackService stacks)
    {
        _lua = lua;
        _stacks = stacks;
    }

    /// <summary>Lists the stack's Lua script tree.</summary>
    [HttpGet]
    public Task<IActionResult> List(string stackId, CancellationToken cancellationToken)
        => StackFileApi.Execute(() => _lua.ListAsync(stackId, cancellationToken));

    /// <summary>Reads a single Lua script file.</summary>
    [HttpGet("content")]
    public Task<IActionResult> Read(string stackId, [FromQuery] string path, CancellationToken cancellationToken)
        => StackFileApi.Execute(() => _lua.ReadAsync(stackId, path, cancellationToken));

    /// <summary>Creates or overwrites a single Lua script file.</summary>
    [HttpPut("content")]
    public Task<IActionResult> Save(string stackId, [FromBody] SaveLuaRequest request, CancellationToken cancellationToken)
        => StackFileApi.Execute(() => _lua.SaveAsync(stackId, request.Path, request.Content ?? string.Empty, cancellationToken));

    /// <summary>Uploads a .zip (folder structure) or a single script file.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(500L * 1024 * 1024)]
    public Task<IActionResult> Upload(
        string stackId,
        [FromForm] IFormFile? file,
        [FromForm] string? path,
        CancellationToken cancellationToken)
        => StackFileApi.Execute(async () =>
        {
            if (file is null || file.Length == 0)
            {
                throw new ArgumentException("No file was uploaded.");
            }

            await using var stream = file.OpenReadStream();
            return await _lua.UploadAsync(stackId, file.FileName, path, stream, cancellationToken);
        });

    /// <summary>Deletes a script file or directory.</summary>
    [HttpDelete("content")]
    public Task<IActionResult> Delete(string stackId, [FromQuery] string path, CancellationToken cancellationToken)
        => StackFileApi.Execute(() => _lua.DeleteAsync(stackId, path, cancellationToken));

    /// <summary>Restarts the game servers so newly-added/edited scripts are loaded.</summary>
    [HttpPost("apply")]
    public Task<IActionResult> Apply(string stackId, CancellationToken cancellationToken)
        => StackFileApi.Execute(async () =>
        {
            var restarted = await _stacks.RestartServerProcessesAsync(stackId, cancellationToken);
            return new { restarted };
        });
}

public sealed class SaveLuaRequest
{
    public string Path { get; set; } = string.Empty;
    public string? Content { get; set; }
}

/// <summary>Shared error mapping for per-stack file editor controllers.</summary>
internal static class StackFileApi
{
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
