using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Reads/writes a stack's server configuration files (worldserver.conf, authserver.conf and module
/// .conf files) under <c>api/stacks/{stackId}/config</c>.
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/config")]
public class StackConfigController : ControllerBase
{
    private readonly IServerConfigService _config;
    private readonly IStackService _stacks;

    public StackConfigController(IServerConfigService config, IStackService stacks)
    {
        _config = config;
        _stacks = stacks;
    }

    /// <summary>Lists the editable .conf files for this stack.</summary>
    [HttpGet]
    public Task<IActionResult> List(string stackId, CancellationToken cancellationToken)
        => StackFileApi.Execute(() => _config.ListAsync(stackId, cancellationToken));

    /// <summary>Reads a single config file's contents.</summary>
    [HttpGet("content")]
    public Task<IActionResult> Read(string stackId, [FromQuery] string path, CancellationToken cancellationToken)
        => StackFileApi.Execute(() => _config.ReadAsync(stackId, path, cancellationToken));

    /// <summary>Saves a config file's contents.</summary>
    [HttpPut("content")]
    public Task<IActionResult> Save(string stackId, [FromBody] SaveConfigRequest request, CancellationToken cancellationToken)
        => StackFileApi.Execute(() => _config.SaveAsync(stackId, request.Path, request.Content ?? string.Empty, cancellationToken));

    /// <summary>Restarts the game servers so config changes take effect.</summary>
    [HttpPost("apply")]
    public Task<IActionResult> Apply(string stackId, CancellationToken cancellationToken)
        => StackFileApi.Execute(async () =>
        {
            var restarted = await _stacks.RestartServerProcessesAsync(stackId, cancellationToken);
            return new { restarted };
        });
}

public sealed class SaveConfigRequest
{
    public string Path { get; set; } = string.Empty;
    public string? Content { get; set; }
}
