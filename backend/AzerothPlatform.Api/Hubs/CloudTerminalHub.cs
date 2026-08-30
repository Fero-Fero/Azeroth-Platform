using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AzerothPlatform.Api.Hubs;

/// <summary>Interactive SSH terminal for the create-stack wizard (bootstrap script paste).</summary>
[Authorize]
public sealed class CloudTerminalHub : Hub
{
    private static readonly ConcurrentDictionary<string, TerminalSession> Sessions = new();

    private readonly IRemoteEngineService _remoteEngine;
    private readonly ICloudSshKeyService _cloudSshKeyService;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly ILogger<CloudTerminalHub> _logger;

    public CloudTerminalHub(
        IRemoteEngineService remoteEngine,
        ICloudSshKeyService cloudSshKeyService,
        ICloudAuditService cloudAuditService,
        ILogger<CloudTerminalHub> logger)
    {
        _remoteEngine = remoteEngine;
        _cloudSshKeyService = cloudSshKeyService;
        _cloudAuditService = cloudAuditService;
        _logger = logger;
    }

    public async Task StartTerminal(DeploymentConfigDto deployment)
    {
        deployment ??= new DeploymentConfigDto();

        if (string.IsNullOrWhiteSpace(deployment.ExternalHost) || string.IsNullOrWhiteSpace(deployment.ExternalSshUser))
        {
            await Clients.Caller.SendAsync("TerminalError", "Remote host and SSH user are required.");
            return;
        }

        string privateKey;
        try
        {
            privateKey = await DeploymentSshKeyResolver.ResolvePrivateKeyAsync(
                deployment,
                _cloudSshKeyService,
                "terminal",
                Context.ConnectionAborted);
        }
        catch (Exception ex)
        {
            await Clients.Caller.SendAsync("TerminalError", ex.Message);
            return;
        }

        await StopTerminalInternalAsync(Context.ConnectionId);

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
        var inputChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        var session = new TerminalSession(sessionCts, inputChannel.Writer);
        Sessions[Context.ConnectionId] = session;

        var connectionId = Context.ConnectionId;
        var clients = Clients;
        var host = deployment.ExternalHost.Trim();
        var user = deployment.ExternalSshUser.Trim();
        var port = deployment.ExternalSshPort <= 0 ? 22 : deployment.ExternalSshPort;

        _logger.LogInformation(
            "Starting cloud terminal for {User}@{Host}:{Port} (connection {ConnectionId})",
            user,
            host,
            port,
            connectionId);

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.TerminalStarted,
                ResourceType = "terminal",
                ResourceId = connectionId,
                Summary = $"Started cloud terminal to {user}@{host}:{port}.",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    host,
                    sshUser = user,
                    port,
                    savedSshKeyId = deployment.SavedSshKeyId,
                }),
            },
            Context.ConnectionAborted);

        _ = Task.Run(async () =>
        {
            try
            {
                await _remoteEngine.RunInteractiveShellAsync(
                    host,
                    port,
                    user,
                    privateKey,
                    async bytes =>
                    {
                        if (sessionCts.IsCancellationRequested)
                        {
                            return;
                        }

                        try
                        {
                            await clients.Client(connectionId)
                                .SendAsync("TerminalOutput", Convert.ToBase64String(bytes), sessionCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // Client disconnected.
                        }
                    },
                    inputChannel.Reader,
                    sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cloud terminal session failed for connection {ConnectionId}", connectionId);
                try
                {
                    await clients.Client(connectionId).SendAsync("TerminalError", ex.Message);
                }
                catch
                {
                    // Client gone.
                }
            }
            finally
            {
                await StopTerminalInternalAsync(connectionId);
                await _cloudAuditService.WriteAsync(
                    new WriteCloudAuditLogRequestDto
                    {
                        EventType = CloudAuditEventTypes.TerminalEnded,
                        ResourceType = "terminal",
                        ResourceId = connectionId,
                        Summary = $"Ended cloud terminal to {user}@{host}:{port}.",
                        MetadataJson = JsonSerializer.Serialize(new { host, sshUser = user, port }),
                    },
                    CancellationToken.None);
                try
                {
                    await clients.Client(connectionId).SendAsync("TerminalClosed");
                }
                catch
                {
                    // Client gone.
                }
            }
        }, CancellationToken.None);

        await Clients.Caller.SendAsync("TerminalStarted");
    }

    public Task SendInput(string base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return Task.CompletedTask;
        }

        if (Sessions.TryGetValue(Context.ConnectionId, out var session))
        {
            try
            {
                var bytes = Convert.FromBase64String(base64);
                session.InputWriter.TryWrite(bytes);
            }
            catch (FormatException)
            {
                // Ignore malformed client payloads.
            }
        }

        return Task.CompletedTask;
    }

    public Task StopTerminal()
        => StopTerminalInternalAsync(Context.ConnectionId);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await StopTerminalInternalAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private static async Task StopTerminalInternalAsync(string connectionId)
    {
        if (!Sessions.TryRemove(connectionId, out var session))
        {
            return;
        }

        await session.Cts.CancelAsync();
        session.InputWriter.TryComplete();
        session.Cts.Dispose();
    }

    private sealed class TerminalSession(CancellationTokenSource cts, ChannelWriter<byte[]> inputWriter)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public ChannelWriter<byte[]> InputWriter { get; } = inputWriter;
    }
}
