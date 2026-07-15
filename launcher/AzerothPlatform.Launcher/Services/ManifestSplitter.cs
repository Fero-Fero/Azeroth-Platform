using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Splits a merged server manifest into base vs overlay views. Promotes standard Blizzard MPQs that
/// older servers may have misclassified as managed so they are always downloaded with the base client.
/// </summary>
internal static class ManifestSplitter
{
    public static (ClientManifest Base, ClientManifest Overlay) Split(ClientManifest full)
    {
        var baseFiles = new List<ManifestFile>();
        var overlayFiles = new List<ManifestFile>();

        foreach (var file in full.Files)
        {
            if (file.Group == ManifestFileGroup.Base || SharedClientDataFiles.IsSharedBaseDataFile(file.RelativePath))
            {
                baseFiles.Add(file.Group == ManifestFileGroup.Base
                    ? file
                    : new ManifestFile
                    {
                        RelativePath = file.RelativePath,
                        Size = file.Size,
                        Sha256 = file.Sha256,
                        Group = ManifestFileGroup.Base
                    });
            }
            else
            {
                overlayFiles.Add(file);
            }
        }

        return (BuildSlice(full, baseFiles), BuildSlice(full, overlayFiles));
    }

    private static ClientManifest BuildSlice(ClientManifest full, List<ManifestFile> files) =>
        new()
        {
            Version = full.Version,
            VerifyToken = full.VerifyToken,
            GeneratedAt = full.GeneratedAt,
            Signature = full.Signature,
            TotalSize = files.Sum(f => f.Size),
            Files = files
        };
}
