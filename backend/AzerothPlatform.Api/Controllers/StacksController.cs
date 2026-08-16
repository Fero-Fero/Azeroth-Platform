using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Exceptions;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Stack management endpoints.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class StacksController : ControllerBase
{
    private readonly IBuildService _buildService;
    private readonly IStackService _stackService;
    private readonly IStackConfigurationValidator _stackConfigurationValidator;
    private readonly IStackDiscoveryService _stackDiscoveryService;
    private readonly IArmoryJobService _armoryJobService;
    private readonly IClientJobService _clientJobService;
    private readonly IStackJobService _stackJobService;
    private readonly IStackDockerService _stackDockerService;
    private readonly IArmoryAccountsService _armoryAccountsService;
    private readonly ICloudFirewallService _cloudFirewallService;

    public StacksController(
        IBuildService buildService,
        IStackService stackService,
        IStackConfigurationValidator stackConfigurationValidator,
        IStackDiscoveryService stackDiscoveryService,
        IArmoryJobService armoryJobService,
        IClientJobService clientJobService,
        IStackJobService stackJobService,
        IStackDockerService stackDockerService,
        IArmoryAccountsService armoryAccountsService,
        ICloudFirewallService cloudFirewallService)
    {
        _buildService = buildService;
        _stackService = stackService;
        _stackConfigurationValidator = stackConfigurationValidator;
        _stackDiscoveryService = stackDiscoveryService;
        _armoryJobService = armoryJobService;
        _clientJobService = clientJobService;
        _stackJobService = stackJobService;
        _stackDockerService = stackDockerService;
        _armoryAccountsService = armoryAccountsService;
        _cloudFirewallService = cloudFirewallService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StackDetailsDto>>> GetAll(CancellationToken cancellationToken = default)
    {
        var stacks = await _stackService.ListAsync(cancellationToken);
        return Ok(stacks);
    }

    /// <summary>
    /// Probes live Docker status for every stack (SSH for VPC stacks). Cached for subsequent list
    /// requests until the next probe or a stack detail refresh.
    /// </summary>
    [HttpPost("probe-status")]
    public async Task<ActionResult<IReadOnlyList<StackDetailsDto>>> ProbeAllStacks(
        CancellationToken cancellationToken = default)
    {
        var stacks = await _stackService.ProbeAllStacksForListAsync(cancellationToken);
        return Ok(stacks);
    }

    [HttpGet("{stackId}")]
    public async Task<ActionResult<StackDetailsDto>> GetById(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _stackService.GetAsync(stackId, cancellationToken);
        return stack is null ? NotFound() : Ok(stack);
    }

    [HttpPost]
    public async Task<ActionResult<CreateStackResponse>> Create(
        [FromBody] StackConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        var validationResult = await _stackConfigurationValidator.ValidateAsync(
            configuration,
            existingStackId: configuration.DraftStackId,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return BadRequest(validationResult);
        }

        try
        {
            var stack = await _stackService.CreateAsync(configuration, cancellationToken);
            return CreatedAtAction(
                nameof(GetById),
                new { stackId = stack.StackId },
                new CreateStackResponse
                {
                    StackId = stack.StackId,
                    Status = stack.Status.ToString()
                });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("setup-draft")]
    public async Task<ActionResult<StackDetailsDto>> SaveSetupDraft(
        [FromBody] StackSetupDraftRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var stack = await _stackService.SaveSetupDraftAsync(request, cancellationToken);
            return Ok(stack);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{stackId}/setup-draft")]
    public async Task<ActionResult<StackSetupDraftDto>> GetSetupDraft(
        string stackId,
        CancellationToken cancellationToken)
    {
        var draft = await _stackService.GetSetupDraftAsync(stackId, cancellationToken);
        return draft is null ? NotFound() : Ok(draft);
    }

    [HttpPut("{stackId}")]
    public async Task<ActionResult<StackDetailsDto>> Update(
        string stackId,
        [FromBody] StackConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        // Validate configuration (allow same ports if editing same stack)
        var validationResult = await _stackConfigurationValidator.ValidateAsync(
            configuration, 
            existingStackId: stackId, 
            cancellationToken: cancellationToken);
        
        if (!validationResult.IsValid)
        {
            return BadRequest(validationResult);
        }

        var updatedStack = await _stackService.UpdateAsync(stackId, configuration, cancellationToken);
        return updatedStack is null ? NotFound() : Ok(updatedStack);
    }

    [HttpPost("{stackId}/reconnect-external")]
    public async Task<ActionResult<StackDetailsDto>> ReconnectExternal(
        string stackId,
        [FromBody] DeploymentConfigDto deployment,
        CancellationToken cancellationToken)
    {
        try
        {
            var stack = await _stackService.ReconnectExternalAsync(stackId, deployment, cancellationToken);
            return stack is null ? NotFound() : Ok(stack);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{stackId}/vpc-security-profile")]
    public async Task<ActionResult<VpcSecurityProfileDto>> GetVpcSecurityProfile(
        string stackId,
        CancellationToken cancellationToken)
    {
        var profile = await _stackService.GetVpcSecurityProfileAsync(stackId, cancellationToken);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpPost("{stackId}/sync-vpc-firewall")]
    public async Task<ActionResult<RemoteSetupResultDto>> SyncVpcFirewall(
        string stackId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _stackService.SyncVpcFirewallAsync(stackId, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{stackId}/sync-cloud-security-group")]
    public async Task<ActionResult<CloudFirewallApplyResultDto>> SyncCloudSecurityGroup(
        string stackId,
        [FromBody] SyncCloudSecurityGroupRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _cloudFirewallService.ApplyStackSecurityGroupRulesAsync(
                stackId,
                request,
                cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{stackId}/vpc-firewall-status")]
    public async Task<ActionResult<VpcFirewallStatusDto>> GetVpcFirewallStatus(
        string stackId,
        CancellationToken cancellationToken)
    {
        var status = await _stackService.GetVpcFirewallStatusAsync(stackId, cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpGet("{stackId}/vpc-ssh-logs")]
    public async Task<ActionResult<VpcSshLogsDto>> GetVpcSshLogs(
        string stackId,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var logs = await _stackService.GetVpcSshLogsAsync(stackId, limit, cancellationToken);
        return logs is null ? NotFound() : Ok(logs);
    }

    [HttpPost("{stackId}/provision-vpc-docker")]
    public async Task<ActionResult<RemoteSetupResultDto>> ProvisionVpcDocker(
        string stackId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _stackService.ProvisionVpcDockerAsync(stackId, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{stackId}/finalize-ssh-hardening")]
    public async Task<ActionResult<RemoteSetupResultDto>> FinalizeSshHardening(
        string stackId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _stackService.FinalizeSshHardeningAsync(stackId, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{stackId}")]
    public async Task<IActionResult> Delete(
        string stackId,
        [FromQuery] bool terminateCloudInstance = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var deleted = await _stackService.DeleteAsync(stackId, terminateCloudInstance, cancellationToken);
            return deleted ? NoContent() : NotFound();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
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

    [HttpPost("validate")]
    public async Task<ActionResult<ValidationResultDto>> Validate(
        [FromBody] StackConfigurationDto configuration,
        [FromQuery] string? existingStackId,
        CancellationToken cancellationToken)
    {
        var validationResult = await _stackConfigurationValidator.ValidateAsync(
            configuration, 
            existingStackId: existingStackId,
            cancellationToken: cancellationToken);
        return Ok(validationResult);
    }

    [HttpPost("{stackId}/build")]
    public async Task<ActionResult<BuildStartedResponse>> StartBuild(
        string stackId,
        [FromBody] StackConfigurationDto? configuration,
        CancellationToken cancellationToken,
        [FromQuery] ConfigMigrationMode configMigrationMode = ConfigMigrationMode.Skip)
    {
        var stack = await _stackService.GetAsync(stackId, cancellationToken);
        if (stack is null)
        {
            return NotFound();
        }

        // Record how the rebuild should reconcile existing .conf files with the freshly built defaults.
        await _stackService.SetConfigMigrationModeAsync(stackId, configMigrationMode, cancellationToken);

        // If no configuration provided, use the existing stack configuration (for rebuilds).
        // Clients that POST an empty JSON object ({}) also mean "rebuild with saved config".
        var buildConfig = IsRebuildWithSavedConfig(configuration) ? stack.Configuration : configuration!;
        
        var buildStatus = await _buildService.StartAsync(stackId, buildConfig, cancellationToken);
        return Ok(new BuildStartedResponse
        {
            BuildId = buildStatus.BuildId,
            Status = buildStatus.CurrentPhase.ToString()
        });
    }

    [HttpGet("{stackId}/build/status")]
    public async Task<ActionResult<BuildStatusDto>> GetBuildStatus(string stackId, CancellationToken cancellationToken)
    {
        var buildStatus = await _buildService.GetStatusAsync(stackId, cancellationToken);
        return buildStatus is null ? NotFound() : Ok(buildStatus);
    }

    [HttpPost("{stackId}/build/cancel")]
    public async Task<IActionResult> CancelBuild(string stackId, CancellationToken cancellationToken)
    {
        var cancelled = await _buildService.CancelAsync(stackId, cancellationToken);
        return cancelled ? Ok() : NotFound();
    }

    [HttpDelete("{stackId}/build/files")]
    public async Task<ActionResult<CleanupResultDto>> CleanupBuildFiles(string stackId, CancellationToken cancellationToken)
    {
        var freedSpace = await _buildService.CleanupAsync(stackId, cancellationToken);
        return Ok(new CleanupResultDto { FreedSpace = freedSpace });
    }

    [HttpGet("{stackId}/docker")]
    public async Task<ActionResult<StackDockerOverviewDto>> GetDockerResources(string stackId, CancellationToken cancellationToken)
    {
        var overview = await _stackDockerService.GetOverviewAsync(stackId, cancellationToken);
        return overview is null ? NotFound() : Ok(overview);
    }

    [HttpDelete("{stackId}/docker/build-files")]
    public async Task<ActionResult<StackDockerDeleteResultDto>> DeleteDockerBuildFiles(
        string stackId,
        CancellationToken cancellationToken)
    {
        var result = await _stackDockerService.DeleteBuildFilesAsync(stackId, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{stackId}/docker/images")]
    public async Task<ActionResult<StackDockerDeleteResultDto>> DeleteDockerImage(
        string stackId,
        [FromQuery] string imageId,
        CancellationToken cancellationToken)
    {
        var result = await _stackDockerService.DeleteImageAsync(stackId, imageId, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{stackId}/docker/images/{imageId}")]
    public async Task<ActionResult<StackDockerDeleteResultDto>> DeleteDockerImageLegacy(
        string stackId,
        string imageId,
        CancellationToken cancellationToken)
    {
        var result = await _stackDockerService.DeleteImageAsync(stackId, Uri.UnescapeDataString(imageId), cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{stackId}/docker/volumes/{volumeName}")]
    public async Task<ActionResult<StackDockerDeleteResultDto>> DeleteDockerVolume(
        string stackId,
        string volumeName,
        CancellationToken cancellationToken)
    {
        var result = await _stackDockerService.DeleteVolumeAsync(stackId, Uri.UnescapeDataString(volumeName), cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{stackId}/docker/volume-audit")]
    public async Task<ActionResult<DockerVolumeAuditDto>> GetDockerVolumeAudit(
        string stackId,
        CancellationToken cancellationToken)
    {
        var audit = await _stackDockerService.GetVolumeAuditAsync(stackId, cancellationToken);
        return audit is null ? NotFound() : Ok(audit);
    }

    [HttpPost("{stackId}/docker/volume-audit/cleanup")]
    public async Task<ActionResult<DockerVolumeCleanupResultDto>> CleanupDockerVolumeAudit(
        string stackId,
        [FromBody] DockerVolumeCleanupRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _stackDockerService.CleanupVolumeAuditAsync(stackId, request, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // Stack lifecycle actions run as detached background jobs so a slow operation (ensuring images,
    // seeding volumes, docker compose up, waiting for services) doesn't block the request and survives
    // navigating away. Endpoints return the initial job status immediately; the UI reattaches via GET
    // job/status and the /hubs/stack-progress stream. Enqueuing while a job is running returns that job,
    // so a second click can't launch a duplicate operation.
    [HttpPost("{stackId}/start")]
    public IActionResult Start(string stackId)
    {
        var job = _stackJobService.Enqueue(stackId, StackJobAction.Start);
        return Accepted(job);
    }

    [HttpPost("{stackId}/start-database")]
    public IActionResult StartDatabase(string stackId)
    {
        var job = _stackJobService.Enqueue(stackId, StackJobAction.StartDatabase);
        return Accepted(job);
    }

    [HttpPost("{stackId}/stop")]
    public IActionResult Stop(string stackId)
    {
        var job = _stackJobService.Enqueue(stackId, StackJobAction.Stop);
        return Accepted(job);
    }

    /// <summary>
    /// Force-stops a stack even when a lifecycle job is already running (e.g. auth crash-looping during start).
    /// </summary>
    [HttpPost("{stackId}/force-stop")]
    public IActionResult ForceStop(string stackId)
    {
        var job = _stackJobService.Enqueue(stackId, StackJobAction.Stop, supersedeRunning: true);
        return Accepted(job);
    }

    [HttpPost("{stackId}/restart")]
    public IActionResult Restart(string stackId)
    {
        var job = _stackJobService.Enqueue(stackId, StackJobAction.Restart);
        return Accepted(job);
    }

    [HttpGet("{stackId}/job/status")]
    public ActionResult<StackJobStatusDto?> GetJobStatus(string stackId)
    {
        return Ok(_stackJobService.GetStatus(stackId));
    }

    // Armory lifecycle actions run as detached background jobs so they survive the request that
    // triggered them (e.g. a browser refresh mid-rebuild). Endpoints return the initial job status
    // immediately; the UI reattaches via GET armory/status and the /hubs/armory-progress stream.
    [HttpPost("{stackId}/armory/start")]
    public IActionResult StartArmory(string stackId)
    {
        var job = _armoryJobService.Enqueue(stackId, ArmoryJobAction.Start);
        return Accepted(job);
    }

    [HttpPost("{stackId}/armory/stop")]
    public IActionResult StopArmory(string stackId)
    {
        var job = _armoryJobService.Enqueue(stackId, ArmoryJobAction.Stop);
        return Accepted(job);
    }

    [HttpGet("{stackId}/armory/status")]
    public ActionResult<ArmoryJobStatusDto?> GetArmoryStatus(string stackId)
    {
        return Ok(_armoryJobService.GetStatus(stackId));
    }

    [HttpPost("{stackId}/client/start")]
    public IActionResult StartClient(string stackId)
    {
        var job = _clientJobService.Enqueue(stackId, ClientJobAction.Start);
        return Accepted(job);
    }

    [HttpPost("{stackId}/client/stop")]
    public IActionResult StopClient(string stackId)
    {
        var job = _clientJobService.Enqueue(stackId, ClientJobAction.Stop);
        return Accepted(job);
    }

    [HttpGet("{stackId}/client/status")]
    public ActionResult<ClientJobStatusDto?> GetClientStatus(string stackId)
    {
        return Ok(_clientJobService.GetStatus(stackId));
    }

    [HttpGet("{stackId}/armory/network")]
    public async Task<ActionResult<ArmoryNetworkConfigDto>> GetArmoryNetwork(string stackId, CancellationToken cancellationToken)
    {
        var config = await _stackService.GetArmoryNetworkAsync(stackId, cancellationToken);
        return config is null ? NotFound() : Ok(config);
    }

    [HttpPut("{stackId}/armory/network")]
    public async Task<ActionResult<ArmoryNetworkConfigDto>> UpdateArmoryNetwork(
        string stackId,
        [FromBody] ArmoryNetworkConfigDto config,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _stackService.UpdateArmoryNetworkAsync(stackId, config, cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{stackId}/armory/accounts-status")]
    public async Task<ActionResult<ArmoryAccountsStatusDto>> GetArmoryAccountsStatus(
        string stackId,
        CancellationToken cancellationToken)
    {
        return Ok(await _armoryAccountsService.GetStatusAsync(stackId, cancellationToken));
    }

    [HttpPost("{stackId}/armory/test-email")]
    public async Task<ActionResult<ArmoryTestEmailResultDto>> SendArmoryTestEmail(
        string stackId,
        [FromBody] ArmoryTestEmailRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _armoryAccountsService.SendTestEmailAsync(stackId, request, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Applies a lifecycle action to a single container/service of the stack.
    /// <paramref name="action"/> is one of: start, stop, restart, recreate.
    /// </summary>
    [HttpPost("{stackId}/services/{service}/{op}")]
    public async Task<IActionResult> ServiceAction(
        string stackId,
        string service,
        string op,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<StackServiceAction>(op, ignoreCase: true, out var parsedAction))
        {
            return BadRequest(new { error = $"Unknown action '{op}'. Expected start, stop, restart or recreate." });
        }

        // The armory has bespoke, potentially long-running logic (image build + DB dependency). Route
        // its per-container actions through the background job service so they survive a page refresh
        // and share the same reattachable status as the top-level Start/Stop Armory controls.
        if (string.Equals(service, "frontend-armory", StringComparison.OrdinalIgnoreCase))
        {
            var armoryAction = parsedAction switch
            {
                StackServiceAction.Stop => ArmoryJobAction.Stop,
                StackServiceAction.Restart => ArmoryJobAction.Restart,
                StackServiceAction.Recreate => ArmoryJobAction.Rebuild,
                _ => ArmoryJobAction.Start
            };
            var job = _armoryJobService.Enqueue(stackId, armoryAction);
            return Accepted(job);
        }

        if (string.Equals(service, "client", StringComparison.OrdinalIgnoreCase))
        {
            var clientAction = parsedAction switch
            {
                StackServiceAction.Stop => ClientJobAction.Stop,
                StackServiceAction.Restart => ClientJobAction.Restart,
                StackServiceAction.Recreate => ClientJobAction.Recreate,
                _ => ClientJobAction.Start
            };
            var job = _clientJobService.Enqueue(stackId, clientAction);
            return Accepted(job);
        }

        try
        {
            var ok = await _stackService.ServiceActionAsync(stackId, service, parsedAction, cancellationToken);
            return ok ? Ok() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get update status for a specific stack
    /// </summary>
    [HttpGet("{stackId}/update-status")]
    public async Task<ActionResult<StackUpdateStatusDto>> GetUpdateStatus(
        string stackId,
        CancellationToken cancellationToken)
    {
        var versionService = HttpContext.RequestServices.GetRequiredService<IStackVersionService>();
        var status = await versionService.GetCachedStatusAsync(stackId, cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }

    /// <summary>
    /// Manually trigger update check for a specific stack (bypasses cache)
    /// </summary>
    [HttpPost("{stackId}/check-updates")]
    public async Task<ActionResult<StackUpdateStatusDto>> CheckUpdatesNow(
        string stackId,
        CancellationToken cancellationToken)
    {
        var versionService = HttpContext.RequestServices.GetRequiredService<IStackVersionService>();
        
        try
        {
            var status = await versionService.CheckAndCacheStatusAsync(stackId, cancellationToken);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update a stack to the latest version (triggers rebuild with existing configuration)
    /// </summary>
    [HttpPost("{stackId}/update")]
    public async Task<ActionResult<BuildStatusDto>> UpdateStack(
        string stackId,
        CancellationToken cancellationToken,
        [FromQuery] ConfigMigrationMode configMigrationMode = ConfigMigrationMode.Merge)
    {
        // Validate stack exists and is not running
        var stack = await _stackService.GetAsync(stackId, cancellationToken);
        if (stack is null)
        {
            return NotFound(new { error = $"Stack {stackId} not found" });
        }

        if (stack.Status == StackStatus.Running)
        {
            return BadRequest(new { error = "Stack must be stopped before updating. Stop the stack and try again." });
        }

        if (stack.Status == StackStatus.Building)
        {
            return BadRequest(new { error = "Stack is currently building. Wait for the build to complete." });
        }

        // Flag this build as an Update so the pipeline snapshots first, then reapplies patch SQL and
        // reboots after the rebuild (a plain rebuild leaves the stack stopped with no snapshot).
        await _stackService.SetPostBuildActionAsync(stackId, PostBuildAction.SnapshotReapplyStart, cancellationToken);

        // Record how the update should reconcile existing .conf files with the freshly built defaults.
        await _stackService.SetConfigMigrationModeAsync(stackId, configMigrationMode, cancellationToken);

        // Trigger rebuild with existing configuration (configuration: null)
        var buildStatus = await _buildService.StartAsync(stackId, configuration: null, cancellationToken);
        return Ok(buildStatus);
    }

    /// <summary>
    /// Discover existing stacks from filesystem and Docker that are not tracked in the database
    /// </summary>
    [HttpGet("discover")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IReadOnlyList<DiscoveredStackDto>))]
    public async Task<ActionResult<IReadOnlyList<DiscoveredStackDto>>> DiscoverStacks(
        CancellationToken cancellationToken)
    {
        var discovered = await _stackDiscoveryService.DiscoverStacksAsync(cancellationToken);
        
        // Filter out stacks already in database
        var existingIds = await _stackService.ListAsync(cancellationToken)
            .ContinueWith(t => t.Result.Select(s => s.StackId).ToHashSet(), cancellationToken);
        
        var newStacks = discovered
            .Where(d => !existingIds.Contains(d.StackId))
            .ToList();
        
        return Ok(newStacks);
    }

    /// <summary>
    /// Import a discovered stack into the manager database
    /// </summary>
    [HttpPost("import/{stackId}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(StackDetailsDto))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StackDetailsDto>> ImportStack(
        string stackId,
        [FromBody] ImportStackRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var imported = await _stackService.ImportDiscoveredStackAsync(stackId, request, cancellationToken);
            return Ok(imported);
        }
        catch (StackNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (StackConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Initialize SOAP admin account for a stack
    /// </summary>
    [HttpPost("{stackId}/initialize-admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> InitializeAdminAccount(
        string stackId,
        CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await _stackService.InitializeAdminAccountAsync(stackId, cancellationToken);
            if (credentials is null)
                return Ok(new { success = true, created = false, message = "Admin account already initialized" });

            return Ok(new
            {
                success = true,
                created = true,
                message = "Admin account created successfully",
                username = credentials.Username,
                password = credentials.Password
            });
        }
        catch (StackNotFoundException ex)
        {
            return NotFound(new { success = false, error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Retrieve stored SOAP admin credentials for credential recovery.
    /// </summary>
    [HttpGet("{stackId}/soap-credentials")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSoapCredentials(string stackId, CancellationToken cancellationToken)
    {
        var credentials = await _stackService.GetSoapCredentialsAsync(stackId, cancellationToken);
        return credentials is null ? NotFound() : Ok(credentials);
    }

    /// <summary>
    /// Retrieve stored MySQL root credentials for credential recovery. Sensitive, audited reveal — the
    /// standard stack detail payload no longer includes the root password.
    /// </summary>
    [HttpGet("{stackId}/database-credentials")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDatabaseCredentials(string stackId, CancellationToken cancellationToken)
    {
        var credentials = await _stackService.GetDatabaseCredentialsAsync(stackId, cancellationToken);
        return credentials is null ? NotFound() : Ok(credentials);
    }

    /// <summary>
    /// Apply module-specific environment variable overrides to a stack.
    /// Changes are persisted and take effect on the next stack restart.
    /// </summary>
    [HttpPost("{stackId}/module-config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApplyModuleConfig(
        string stackId,
        [FromBody] ApplyModuleConfigRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EnvVars == null || request.EnvVars.Count == 0)
        {
            return BadRequest(new { error = "At least one environment variable is required." });
        }

        try
        {
            await _stackService.ApplyModuleConfigAsync(stackId, request.EnvVars, cancellationToken);
            return Ok(new { success = true, message = "Module configuration applied. Restart the stack to take effect." });
        }
        catch (StackNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// True when the client omitted a body or sent an empty object meaning "rebuild with the stack's
    /// persisted configuration" rather than a fresh build definition.
    /// </summary>
    private static bool IsRebuildWithSavedConfig(StackConfigurationDto? configuration) =>
        configuration is null
        || (string.IsNullOrWhiteSpace(configuration.StackName)
            && configuration.ModuleIds.Count == 0
            && string.IsNullOrWhiteSpace(configuration.Advanced.RealmName));
}

public record ApplyModuleConfigRequest(Dictionary<string, string> EnvVars);
