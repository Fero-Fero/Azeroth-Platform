using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Health check endpoint
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IDockerService _dockerService;
    private readonly IGitService _gitService;

    public HealthController(
        AzerothCoreDbContext dbContext,
        IDockerService dockerService,
        IGitService gitService)
    {
        _dbContext = dbContext;
        _dockerService = dockerService;
        _gitService = gitService;
    }

    /// <summary>
    /// Get health status
    /// </summary>
    /// <returns>Health status with timestamp</returns>
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var databaseHealthy = await _dbContext.Database.CanConnectAsync(cancellationToken);
        var dockerHealthy = await IsDockerHealthyAsync(cancellationToken);
        var gitHealthy = await IsGitHealthyAsync(cancellationToken);

        var overallStatus = databaseHealthy && dockerHealthy && gitHealthy ? "healthy" : "degraded";

        // Anonymous callers (this endpoint is public for load balancers/uptime checks) get only the
        // overall status — the per-dependency breakdown is internal detail useful to an attacker for
        // fingerprinting, so it is limited to authenticated admins.
        if (User.Identity?.IsAuthenticated != true)
        {
            return Ok(new { status = overallStatus, timestamp = DateTime.UtcNow });
        }

        return Ok(new
        {
            status = overallStatus,
            timestamp = DateTime.UtcNow,
            dependencies = new
            {
                database = databaseHealthy ? "healthy" : "unhealthy",
                docker = dockerHealthy ? "healthy" : "unhealthy",
                git = gitHealthy ? "healthy" : "unhealthy"
            }
        });
    }

    private async Task<bool> IsDockerHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _dockerService.IsDockerAvailableAsync(cancellationToken);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> IsGitHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _gitService.IsGitAvailableAsync(cancellationToken);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
