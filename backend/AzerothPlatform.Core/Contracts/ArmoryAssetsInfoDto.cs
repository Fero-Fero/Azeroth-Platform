namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Summary of the operator-uploaded armory asset bundles, shown on the global Armory Data page and
/// the per-stack armory-data tab.
/// </summary>
public sealed class ArmoryAssetsInfoDto
{
    /// <summary>True when a model-viewer dataset has been uploaded (its <c>meta/</c> folder exists).</summary>
    public bool DataUploaded { get; set; }

    /// <summary>True when a static web-asset bundle has been uploaded.</summary>
    public bool StaticUploaded { get; set; }

    /// <summary>Total size on disk of the uploaded model-viewer dataset, in bytes.</summary>
    public long DataSize { get; set; }

    /// <summary>Total size on disk of the uploaded static bundle, in bytes.</summary>
    public long StaticSize { get; set; }

    /// <summary>Number of files in the uploaded model-viewer dataset.</summary>
    public int DataFileCount { get; set; }

    /// <summary>Number of files in the uploaded static bundle.</summary>
    public int StaticFileCount { get; set; }

    /// <summary>Which expected top-level dataset folders are present (e.g. bone, dbc, meta, mo3, textures).</summary>
    public List<string> DataFolders { get; set; } = new();

    /// <summary>
    /// True when static assets have been uploaded/changed since the shared armory image was last built,
    /// so the armory image must be rebuilt for the new static assets to take effect.
    /// </summary>
    public bool StaticRebuildPending { get; set; }
}
