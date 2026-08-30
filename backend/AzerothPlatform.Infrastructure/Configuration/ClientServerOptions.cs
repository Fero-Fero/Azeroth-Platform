namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Configuration for the per-stack client-server (<c>azeroth-platform-client</c>) container: the
/// shared image name, the backend source baked into the manager image (to build that image), and the
/// port the container listens on. Mirrors <see cref="ArmoryOptions"/>.
/// </summary>
public sealed class ClientServerOptions
{
    public const string SectionName = "ClientServer";

    /// <summary>Docker image tag built once and referenced by every stack's client service.</summary>
    public string ImageName { get; set; } = "azeroth-platform-client:latest";

    /// <summary>
    /// Path to the backend source baked into the manager image (see Dockerfile <c>COPY backend/</c>).
    /// Must contain the ClientServer, ClientManifest and Core projects so the image build resolves
    /// their project references.
    /// </summary>
    public string SourcePath { get; set; } = "/app/backend-src";

    /// <summary>
    /// Writable working directory (under the data volume) the source is copied into before build, so
    /// the docker build gets a container-visible context path.
    /// </summary>
    public string WorkPath { get; set; } = "/app/data/client-server-build";

    /// <summary>Dockerfile path relative to the build context (the ClientServer project folder).</summary>
    public string DockerfileRelativePath { get; set; } = "AzerothPlatform.ClientServer/Dockerfile";

    /// <summary>Port the client-server listens on inside the container.</summary>
    public int ContainerPort { get; set; } = 8090;
}
