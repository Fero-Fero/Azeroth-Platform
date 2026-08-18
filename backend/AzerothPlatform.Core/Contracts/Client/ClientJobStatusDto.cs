using System.Text.Json.Serialization;

namespace AzerothPlatform.Core.Contracts;

/// <summary>The client file-server lifecycle operation a background job is performing.</summary>
public enum ClientJobAction
{
    Start,
    Stop,
    Restart,
    Recreate,
}

/// <summary>Coarse-grained phase of a client background job.</summary>
public enum ClientJobPhase
{
    Starting,
    Stopping,
    Restarting,
    Recreating,
    Completed,
    Failed,
}

/// <summary>
/// Status of a per-stack client background job. The client (re)build/start/stop runs detached from the
/// HTTP request that triggered it, so this snapshot is what the UI polls and receives over SignalR to
/// reattach after a page refresh.
/// </summary>
public class ClientJobStatusDto
{
    public string StackId { get; set; } = string.Empty;

    public string JobId { get; set; } = string.Empty;

    public ClientJobAction Action { get; set; }

    public ClientJobPhase Phase { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? Error { get; set; }

    public bool? Success { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    [JsonInclude]
    public bool IsRunning => Phase is not (ClientJobPhase.Completed or ClientJobPhase.Failed);
}
