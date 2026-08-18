namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Receives live progress from a running patch apply so a background runner can stream log lines and
/// stage transitions to a status endpoint and a persisted trace-log file.
/// </summary>
public interface IApplyProgressSink
{
    /// <summary>Reports a single log line.</summary>
    void Log(string line);

    /// <summary>Reports that the apply has entered a new stage (e.g. "sql", "dbc", "build-patch-d").</summary>
    void Stage(string stage);
}
