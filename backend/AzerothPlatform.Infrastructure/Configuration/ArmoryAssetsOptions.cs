namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Location of the per-stack operator-uploaded armory asset bundles. Unlike the armory source baked
/// into the manager image, these live under the persistent data volume so they survive restarts.
/// Each stack has its own subtree (<c>{RootPath}/stacks/{stackId}</c>). Everything lives under a single
/// <c>static/</c> root so uploads and the image build read one place:
/// <list type="bullet">
/// <item><c>.../static</c> — the armory web static assets (armory.static.zip): <c>js/</c>, <c>css/</c>,
/// <c>img/</c> (incl. <c>img/wow-icons</c>). Baked into the stack's armory image on rebuild.</item>
/// <item><c>.../static/data</c> — the 3D model-viewer dataset (armory.data.zip + armory.textures.zip):
/// <c>bone/</c>, <c>dbc/</c>, <c>dbc_transmog/</c>, <c>meta/</c>, <c>mo3/</c>, <c>textures/</c>,
/// <c>progression/</c>, background PNGs. The small parts (dbc, progression, backgrounds) are baked into
/// the image; the heavy parts (mo3/meta/bone/textures) are excluded from the image and served to
/// browsers by the stack's <c>armory-assets</c> sidecar from that stack's assets volume.</item>
/// </list>
/// When present these take precedence over the assets baked into the manager image.
/// </summary>
public sealed class ArmoryAssetsOptions
{
    public const string SectionName = "ArmoryAssets";

    /// <summary>Persistent, writable root for uploaded armory assets (lives in the data volume).</summary>
    public string RootPath { get; set; } = "/app/data/armory-assets";

    /// <summary>Subdirectory holding the uploaded model-viewer dataset.</summary>
    public string DataDirName { get; set; } = "data";

    /// <summary>Subdirectory holding the uploaded static web assets.</summary>
    public string StaticDirName { get; set; } = "static";

    /// <summary>
    /// Marker file written when a static bundle is uploaded and cleared once the assets are baked into
    /// the stack's armory image. Cleared at the single point where the bake happens
    /// (<c>ArmoryImageService.BuildImageAsync</c>) so every rebuild path reconciles it.
    /// </summary>
    public const string RebuildMarkerName = ".static-rebuild-pending";

    /// <summary>Persistent, writable root for a specific stack's uploaded armory assets.</summary>
    public string StackRootPath(string stackId) => Path.Combine(RootPath, "stacks", stackId);

    /// <summary>Absolute path to a stack's uploaded static web assets directory (the single asset root).</summary>
    public string StaticPathFor(string stackId) => Path.Combine(StackRootPath(stackId), StaticDirName);

    /// <summary>
    /// Absolute path to a stack's uploaded model-viewer dataset directory. Nested under the static
    /// directory (<c>.../static/data</c>) so a single folder holds every armory asset and the image
    /// build reads one place.
    /// </summary>
    public string DataPathFor(string stackId) => Path.Combine(StaticPathFor(stackId), DataDirName);

    /// <summary>Absolute path to a stack's "static rebuild pending" marker file.</summary>
    public string RebuildMarkerPath(string stackId) => Path.Combine(StackRootPath(stackId), RebuildMarkerName);
}
