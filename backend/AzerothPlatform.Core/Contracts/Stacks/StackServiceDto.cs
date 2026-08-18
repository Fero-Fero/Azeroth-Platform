namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// A single manageable Docker Compose service of a stack (e.g. the database, auth/world servers or
/// the armory), annotated with its current runtime state. Unlike <see cref="ContainerStatusDto"/>
/// this is always present for the stack's canonical services even when the underlying container
/// does not exist yet (state <c>absent</c>), so the UI can offer a Start action.
/// </summary>
public sealed class StackServiceDto
{
    /// <summary>Docker Compose service name used for lifecycle commands, e.g. <c>ac-worldserver</c>.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Human-friendly label shown in the UI, e.g. <c>World Server</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Actual container name when it exists, otherwise empty.</summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>
    /// Runtime state: <c>running</c>, <c>exited</c>/<c>stopped</c>/<c>created</c>/<c>restarting</c>
    /// (whatever docker reports) or <c>absent</c> when no container exists for the service.
    /// </summary>
    public string State { get; set; } = "absent";

    /// <summary>Health check status (healthy/unhealthy/unknown) when the container exists.</summary>
    public string Health { get; set; } = string.Empty;

    /// <summary>When the container was created/started, when it exists.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Grouping used by the UI to decide which actions make sense: <c>core</c> (db/auth/world),
    /// <c>armory</c>, <c>init</c> (one-shot bootstrap containers) or <c>utility</c>.
    /// </summary>
    public string Category { get; set; } = "core";
}

/// <summary>Lifecycle action applied to a single stack service.</summary>
public enum StackServiceAction
{
    /// <summary>Create/start the service (<c>docker compose up -d &lt;svc&gt;</c>).</summary>
    Start,

    /// <summary>Stop the service, keeping the container (<c>docker compose stop &lt;svc&gt;</c>).</summary>
    Stop,

    /// <summary>Restart the running container (<c>docker compose restart &lt;svc&gt;</c>).</summary>
    Restart,

    /// <summary>
    /// Rebuild &amp; restart: recreate the container from its current image and the latest
    /// generated config (<c>docker compose up -d --force-recreate &lt;svc&gt;</c>).
    /// </summary>
    Recreate
}
