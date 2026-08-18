namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Response returned after a build is started.
/// </summary>
public class BuildStartedResponse
{
    public string BuildId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}
