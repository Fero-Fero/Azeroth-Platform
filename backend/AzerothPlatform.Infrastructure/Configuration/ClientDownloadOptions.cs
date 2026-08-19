namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Optional URL used by the Client tab "Download Client" button and Express auto-provision.
/// Leave blank until a public base-client archive is configured.
/// </summary>
public sealed class ClientDownloadOptions
{
    public const string SectionName = "ClientDownload";

    /// <summary>
    /// Public archive URL or Google Drive folder/file share. Empty disables download.
    /// </summary>
    public string BaseClientUrl { get; set; } = string.Empty;
}
