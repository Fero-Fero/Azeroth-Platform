namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Optional URL used by Express auto-provision when the Client tab has not uploaded a base yet.
/// Leave blank to skip Express auto-download (the Client tab can download from a pasted URL).
/// </summary>
public sealed class ClientDownloadOptions
{
    public const string SectionName = "ClientDownload";

    /// <summary>
    /// Public archive URL or Google Drive folder/file share. Empty disables download.
    /// </summary>
    public string BaseClientUrl { get; set; } = string.Empty;
}
