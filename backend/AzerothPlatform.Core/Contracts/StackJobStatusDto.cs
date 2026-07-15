using System.Text.Json.Serialization;

namespace AzerothPlatform.Core.Contracts;

/// <summary>The stack lifecycle operation a background job is performing.</summary>
public enum StackJobAction
{
    Start,
    StartDatabase,
    Stop,
    Restart
}

/// <summary>Coarse-grained phase of a stack lifecycle background job.</summary>
public enum StackJobPhase
{
    Starting,
    StartingDatabase,
    Stopping,
    Restarting,
    Completed,
    Failed
}

/// <summary>
/// Status of a per-stack lifecycle background job (start/stop/restart/start-database). These operations
/// run detached from the HTTP request that triggered them (they can take minutes: ensuring images,
/// seeding volumes, <c>docker compose up</c>, waiting for services). This snapshot is what the UI polls
/// (GET) and receives over SignalR to reattach to an in-flight operation after navigating or refreshing,
/// and it lets the backend reject a second trigger while one is already running.
/// </summary>
public class StackJobStatusDto
{
    public string StackId { get; set; } = string.Empty;

    /// <summary>Unique id for this run (changes each time a new job is enqueued).</summary>
    public string JobId { get; set; } = string.Empty;

    public StackJobAction Action { get; set; }

    public StackJobPhase Phase { get; set; }

    /// <summary>Human-readable description of the current step, shown in the UI.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Populated when <see cref="Phase"/> is <see cref="StackJobPhase.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Final outcome once the job is no longer running; null while in progress.</summary>
    public bool? Success { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>True while the job is still executing (not Completed/Failed).</summary>
    [JsonInclude]
    public bool IsRunning => Phase is not (StackJobPhase.Completed or StackJobPhase.Failed);
}
