namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Configuration for compiling the desktop launcher in a docker sidecar and distributing the exe.
/// </summary>
public sealed class LauncherBuildOptions
{
    public const string SectionName = "Launcher";

    /// <summary>.NET SDK image used to cross-publish the win-x64 launcher.</summary>
    public string SdkImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    /// <summary>
    /// Path to the launcher source baked into the manager image (see Dockerfile <c>COPY launcher/</c>).
    /// </summary>
    public string SourcePath { get; set; } = "/app/launcher-src";

    /// <summary>
    /// Writable working directory (under the data volume) the source is copied into before publish,
    /// so the sidecar can bind-mount a host-visible path.
    /// </summary>
    public string WorkPath { get; set; } = "/app/data/launcher-build";

    /// <summary>Directory (under the data volume) the built launcher exe is placed in for download.</summary>
    public string DistPath { get; set; } = "/app/data/launcher-dist";

    /// <summary>Csproj (relative to <see cref="SourcePath"/>) to publish.</summary>
    public string ProjectRelativePath { get; set; } = "AzerothPlatform.Launcher/AzerothPlatform.Launcher.csproj";

    /// <summary>File name of the produced single-file executable.</summary>
    public string ExecutableName { get; set; } = "AzerothPlatformLauncher.exe";
}
