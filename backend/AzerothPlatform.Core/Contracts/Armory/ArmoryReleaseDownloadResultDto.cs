namespace AzerothPlatform.Core.Contracts;

/// <summary>Result of downloading armory release bundles from GitHub and applying them to a stack.</summary>
public sealed class ArmoryReleaseDownloadResultDto
{
    public ArmoryAssetsInfoDto Info { get; set; } = new();

    /// <summary>GitHub release tag that was applied (e.g. <c>Armory</c>).</summary>
    public string ReleaseTag { get; set; } = string.Empty;

    /// <summary>Asset file names downloaded and applied (e.g. armory.data.zip).</summary>
    public List<string> DownloadedAssets { get; set; } = [];

    /// <summary>Expected assets that were not present on the release.</summary>
    public List<string> MissingAssets { get; set; } = [];
}
