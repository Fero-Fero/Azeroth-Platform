namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Configuration for the per-stack armory (frontend-armory) container: the shared image name, the
/// source baked into the manager image (to build that image), and the platform URLs injected into
/// each stack's armory so it can fetch news and link to the launcher download.
/// </summary>
public sealed class ArmoryOptions
{
    public const string SectionName = "Armory";

    /// <summary>Docker image tag built once and referenced by every stack's armory service.</summary>
    public string ImageName { get; set; } = "azeroth-platform-armory:latest";

    /// <summary>
    /// Path to the armory source baked into the manager image (see Dockerfile <c>COPY frontend-armory/</c>).
    /// </summary>
    public string SourcePath { get; set; } = "/app/armory-src";

    /// <summary>
    /// Writable working directory (under the data volume) the source is copied into before build,
    /// so the docker build gets a host-visible context path.
    /// </summary>
    public string WorkPath { get; set; } = "/app/data/armory-build";

    /// <summary>
    /// Platform API base URL the armory container uses for server-side calls (e.g. news). Must be
    /// reachable from inside a container on the host, hence host.docker.internal by default.
    /// </summary>
    public string PlatformApiUrl { get; set; } = "http://host.docker.internal:8080";

    /// <summary>
    /// Public platform base URL used for browser-facing links (e.g. the launcher download button on
    /// the armory's Connect page). Blank falls back to <see cref="PlatformApiUrl"/>.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL the armory proxies its <c>/data/*</c> model-viewer routes to. Each stack gets its own
    /// <c>armory-assets</c> sidecar on the same compose project, so the armory reaches it by service
    /// name. Only injected when the model-viewer dataset actually exists (see <see cref="AssetsHostPath"/>).
    /// </summary>
    public string AssetProxyUrl { get; set; } = "http://armory-assets";

    /// <summary>
    /// Host path to the shared 3D model-viewer dataset (<c>frontend-armory/data</c>) that each
    /// stack's <c>armory-assets</c> sidecar serves. Blank means "derive it" from the manager's data
    /// path (the sibling <c>frontend-armory/data</c> next to the mounted <c>data</c> directory).
    /// </summary>
    public string AssetsHostPath { get; set; } = string.Empty;
}
