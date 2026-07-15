using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Api.Hubs;

/// <summary>
/// SignalR hub for streaming container logs.
/// </summary>
[Authorize]
public class ContainerLogsHub : Hub
{
    private readonly IDockerService _dockerService;
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly ILogger<ContainerLogsHub> _logger;
    private static readonly Dictionary<string, CancellationTokenSource> _streamingTasks = new();

    public ContainerLogsHub(
        IDockerService dockerService,
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine,
        ILogger<ContainerLogsHub> logger)
    {
        _dockerService = dockerService;
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
        _logger = logger;
    }

    public async Task StartStreamingLogs(string stackId, string containerName, int tail = 500)
    {
        try
        {
            _logger.LogInformation(
                "Client {ConnectionId} requesting logs for container {ContainerName} in stack {StackId}",
                Context.ConnectionId,
                containerName,
                stackId);

            // Validate stack exists
            var stack = await _dbContext.ManagedStacks.FirstOrDefaultAsync(s => s.Id == stackId);
            if (stack == null)
            {
                await Clients.Caller.SendAsync("LogStreamError", $"Stack {stackId} not found");
                return;
            }

            // For external stacks, target the remote engine over SSH so the UI streams the remote
            // containers; local stacks use the manager's default engine.
            string? dockerContext = stack.DeploymentTarget == DeploymentTarget.External
                ? await _remoteEngine.EnsureContextAsync(stack)
                : null;

            // Get containers for this stack to validate the container belongs to it
            var composeProjectName = $"acore-{stackId}";
            var containers = await _dockerService.ListContainersAsync(composeProjectName, dockerContext);
            
            _logger.LogInformation(
                "Found {ContainerCount} containers for project {ProjectName}. Looking for: {TargetContainer}. Available: {AvailableContainers}",
                containers.Count,
                composeProjectName,
                containerName,
                string.Join(", ", containers.Select(c => c.Name)));
            
            var container = containers.FirstOrDefault(c => c.Name == containerName);
            
            if (container == null)
            {
                await Clients.Caller.SendAsync("LogStreamError", $"Container {containerName} not found in stack {stackId}");
                return;
            }

            // Cancel any existing stream for this connection
            if (_streamingTasks.TryGetValue(Context.ConnectionId, out var existingCts))
            {
                await existingCts.CancelAsync();
                _streamingTasks.Remove(Context.ConnectionId);
            }

            // Create new cancellation token
            var cts = new CancellationTokenSource();
            _streamingTasks[Context.ConnectionId] = cts;

            await Clients.Caller.SendAsync("LogStreamStarted", containerName, tail);

            _logger.LogInformation("Resolved container {ContainerName} to ID {ContainerId}", 
                containerName, container.ContainerId);

            // Capture hub context to avoid accessing disposed Hub
            var hubContext = Context;
            var clients = Clients;
            
            // Stream logs in background task with auto-reconnect on container restart
            _ = Task.Run(async () =>
            {
                var retryCount = 0;
                const int maxRetries = -1; // Infinite retries
                const int retryDelayMs = 2000; // 2 seconds between retries
                
                while (!cts.Token.IsCancellationRequested && (maxRetries == -1 || retryCount < maxRetries))
                {
                    try
                    {
                        if (retryCount > 0)
                        {
                            _logger.LogInformation("Reconnecting to container {ContainerId} logs (attempt {RetryCount})", 
                                container.ContainerId, retryCount + 1);
                            await Task.Delay(retryDelayMs, cts.Token);
                        }
                        
                        await _dockerService.StreamContainerLogsAsync(
                            container.ContainerId,
                            tail: retryCount == 0 ? tail : 0, // Only use tail on first connect
                            async (message, isError) =>
                            {
                                if (!cts.Token.IsCancellationRequested)
                                {
                                    try
                                    {
                                        await clients.Client(hubContext.ConnectionId)
                                            .SendAsync("LogReceived", message, isError, cts.Token);
                                    }
                                    catch (ObjectDisposedException)
                                    {
                                        // Client disconnected, stop processing
                                        await cts.CancelAsync();
                                    }
                                }
                            },
                            dockerContext,
                            cts.Token);

                        // If we get here, the stream ended normally (container stopped or deleted)
                        _logger.LogInformation("Log stream ended for container {ContainerId}, will retry...", container.ContainerId);
                        retryCount++;
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Log streaming cancelled for connection {ConnectionId}", hubContext.ConnectionId);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error streaming logs for connection {ConnectionId}, will retry...", hubContext.ConnectionId);
                        retryCount++;
                    }
                }
                
                // Cleanup
                _streamingTasks.Remove(hubContext.ConnectionId);
                cts.Dispose();
                
                if (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await clients.Client(hubContext.ConnectionId)
                            .SendAsync("LogStreamEnded", "Container log stream ended");
                    }
                    catch (ObjectDisposedException)
                    {
                        // Client already disconnected
                    }
                }
            }, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting log stream");
            await Clients.Caller.SendAsync("LogStreamError", $"Failed to start log stream: {ex.Message}");
        }
    }

    public Task StopStreamingLogs()
    {
        if (_streamingTasks.TryGetValue(Context.ConnectionId, out var cts))
        {
            cts.Cancel();
            _streamingTasks.Remove(Context.ConnectionId);
        }
        
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Clean up streaming task when client disconnects
        if (_streamingTasks.TryGetValue(Context.ConnectionId, out var cts))
        {
            await cts.CancelAsync();
            _streamingTasks.Remove(Context.ConnectionId);
            cts.Dispose();
        }

        await base.OnDisconnectedAsync(exception);
    }
}
