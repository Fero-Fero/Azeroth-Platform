using System.Text.Json.Serialization;

namespace AzerothPlatform.Core.Contracts;

/// <summary>The armory lifecycle operation a background job is performing.</summary>
public enum ArmoryJobAction
{
    Start,
    Stop,
    Restart,
    Rebuild,

    /// <summary>
    /// Extract the stack's live server DBCs, convert them to the CSVs the armory reads, bake them into
    /// the armory image and restart it. Triggered when "Load DBCs" is enabled for the armory.
    /// </summary>
    SyncDbc
}

/// <summary>Coarse-grained phase of an armory background job.</summary>
public enum ArmoryJobPhase
{
    Starting,
    Stopping,
    Restarting,
    Rebuilding,
    SyncingDbc,
    Completed,
    Failed
}

/// <summary>
/// Status of a per-stack armory background job. The armory (re)build/start/stop runs detached from the
/// HTTP request that triggered it, so this snapshot is what the UI polls (GET) and receives over SignalR
/// to reattach to an in-flight operation after a page refresh.
/// </summary>
public class ArmoryJobStatusDto
{
    public string StackId { get; set; } = string.Empty;

    /// <summary>Unique id for this run (changes each time a new job is enqueued).</summary>
    public string JobId { get; set; } = string.Empty;

    public ArmoryJobAction Action { get; set; }

    public ArmoryJobPhase Phase { get; set; }

    /// <summary>Human-readable description of the current step, shown in the UI.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Timestamped progress log lines for the running operation (e.g. per-table DBC conversion,
    /// image rebuild). Included in every status snapshot so the UI can render a live log panel and
    /// reattach to the full history after a page refresh.
    /// </summary>
    public List<string> RecentLogs { get; set; } = new();

    /// <summary>Populated when <see cref="Phase"/> is <see cref="ArmoryJobPhase.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Final outcome once the job is no longer running; null while in progress.</summary>
    public bool? Success { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>True while the job is still executing (not Completed/Failed).</summary>
    [JsonInclude]
    public bool IsRunning => Phase is not (ArmoryJobPhase.Completed or ArmoryJobPhase.Failed);
}
