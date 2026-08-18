namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Configuration for Docker access and build storage.
/// </summary>
public sealed class DockerOptions
{
    public const string SectionName = "Docker";

    public string SocketPath { get; set; } = "unix:///var/run/docker.sock";

    public string BuildsPath { get; set; } = "/builds";

    /// <summary>
    /// Name of the Docker-managed named volume backing the manager's data directory (the parent of
    /// <see cref="BuildsPath"/>, e.g. <c>/app/data</c>). When set and this volume exists, local volume
    /// seeding uses a fast daemon-side volume-to-volume copy instead of streaming a tar through the CLI.
    /// Leave empty for non-containerized deployments (falls back to tar streaming).
    /// </summary>
    public string? DataVolumeName { get; set; } = "azeroth-platform-data";

    /// <summary>
    /// Docker Compose command format. Options: "plugin" (docker compose), "standalone" (docker-compose), or "auto" (detect).
    /// </summary>
    public string ComposeCommand { get; set; } = "auto";

    /// <summary>
    /// Host interface that player-facing stack HTTP services (armory website, client file server) publish
    /// on. Defaults to loopback (<c>127.0.0.1</c>) so a fresh deployment is not exposed to the internet by
    /// accident; set to a private interface IP, a public IP, or <c>0.0.0.0</c> (all interfaces) once a
    /// reverse proxy / firewall is in place. The game protocol ports (auth 3724, world 8085) are always
    /// published on all interfaces because the game client connects to them directly.
    /// </summary>
    public string PublishBindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Host interface that the data-plane ports (MySQL 3306, worldserver SOAP 7878) publish on. Defaults
    /// to loopback so these management ports are never reachable from the internet. The manager and armory
    /// reach them via <c>host.docker.internal</c>: on Docker Desktop that resolves to the host loopback, so
    /// <c>127.0.0.1</c> works out of the box; on a Linux host where the manager runs in a container this
    /// must be set to the docker bridge-gateway IP (e.g. <c>172.17.0.1</c>) or a private LAN IP so the
    /// container can reach it, while still keeping it off the public internet.
    /// </summary>
    public string DataPlaneBindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Host interface on the <em>remote engine</em> that an external stack's data-plane ports (MySQL 3306,
    /// worldserver SOAP 7878) publish on. External stacks run on a remote Docker engine and the manager
    /// reaches their database/SOAP over the network via the stack's <c>ExternalHost</c>. Because loopback
    /// on the remote is not reachable by the manager, these ports cannot simply be pinned to loopback the
    /// way local stacks are. Leaving this empty (the default) publishes them on <b>all interfaces</b>
    /// (<c>0.0.0.0</c>) of the remote host - i.e. potentially the public internet. Set this to the remote's
    /// private/VPC interface IP (typically the same address the manager uses as <c>ExternalHost</c>, e.g. a
    /// WireGuard/VPC address) to keep MySQL/SOAP off the public internet while remaining reachable by the
    /// manager. The game protocol ports (auth 3724, world 8085) and player-facing HTTP (armory, client)
    /// always stay on all interfaces for external stacks because clients connect to them directly.
    /// </summary>
    public string? ExternalDataPlaneBindAddress { get; set; }
}
