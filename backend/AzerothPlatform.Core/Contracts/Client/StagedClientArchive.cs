namespace AzerothPlatform.Core.Contracts;

/// <summary>Where an uploaded base-client archive was parked while it waits for its install job.</summary>
public enum StagedClientArchiveKind
{
    /// <summary>A Docker volume holding <c>upload.archive</c> at its root, extracted by the engine.</summary>
    WorkVolume,

    /// <summary>A file on manager disk, for formats the engine-side extractor cannot open (RAR).</summary>
    ManagerDisk,
}

/// <summary>
/// Identifies a staged upload so the HTTP request that received it and the background job that
/// installs it can refer to the same bytes. Serialised to a single string because it travels through
/// the job queue as one opaque token.
/// </summary>
/// <param name="Kind">Whether the archive sits in a Docker volume or on manager disk.</param>
/// <param name="Location">The volume name, or the absolute file path.</param>
public readonly record struct StagedClientArchive(StagedClientArchiveKind Kind, string Location)
{
    private const string VolumePrefix = "volume:";

    public static StagedClientArchive InWorkVolume(string volumeName) =>
        new(StagedClientArchiveKind.WorkVolume, volumeName);

    public static StagedClientArchive OnManagerDisk(string archivePath) =>
        new(StagedClientArchiveKind.ManagerDisk, archivePath);

    /// <summary>
    /// Parses a token produced by <see cref="ToString"/>. An unprefixed value is treated as a manager
    /// disk path so tokens issued before work-volume staging existed still resolve.
    /// </summary>
    public static StagedClientArchive Parse(string token)
    {
        var value = (token ?? string.Empty).Trim();
        return value.StartsWith(VolumePrefix, StringComparison.Ordinal)
            ? InWorkVolume(value[VolumePrefix.Length..])
            : OnManagerDisk(value);
    }

    public override string ToString() =>
        Kind == StagedClientArchiveKind.WorkVolume ? VolumePrefix + Location : Location;
}
