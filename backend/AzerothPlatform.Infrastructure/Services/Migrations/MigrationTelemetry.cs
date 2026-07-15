using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services.Migrations;

/// <summary>
/// Tracing primitives for the migration/patch pipeline. The <see cref="ActivitySource"/> emits
/// spans for the overall apply, each stage, and every docker/process invocation, with tags such as
/// stack id, patch key, exit codes and durations.
/// </summary>
/// <remarks>
/// This is OpenTelemetry-ready: register the source (<see cref="SourceName"/>) with an OTel
/// TracerProvider to export to a real backend. Until then, <see cref="RegisterLoggingListener"/>
/// wires a lightweight <see cref="ActivityListener"/> that mirrors completed spans to the logger,
/// so traces (trace id, span id, duration, tags) are visible in the console without extra
/// infrastructure. Without any listener, <c>StartActivity</c> is a cheap no-op.
/// </remarks>
public static class MigrationTelemetry
{
    /// <summary>Name of the tracing source; register this with OpenTelemetry to export spans.</summary>
    public const string SourceName = "AzerothPlatform.Migrations";

    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");

    /// <summary>
    /// Registers a listener that samples all migration spans and logs each completed span. Keep the
    /// returned handle alive for the lifetime of the application. Safe to call once at startup.
    /// </summary>
    public static IDisposable RegisterLoggingListener(ILogger logger)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (!logger.IsEnabled(LogLevel.Information))
                {
                    return;
                }

                var tags = activity.TagObjects.Any()
                    ? " " + string.Join(" ", activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))
                    : string.Empty;

                logger.LogInformation(
                    "span {Operation} [{TraceId}/{SpanId}] {Status} in {DurationMs} ms{Tags}",
                    activity.OperationName,
                    activity.TraceId,
                    activity.SpanId,
                    activity.Status,
                    activity.Duration.TotalMilliseconds.ToString("0"),
                    tags);
            }
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
