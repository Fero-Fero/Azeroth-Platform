namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Configuration options for the stack update checker background service
/// </summary>
public class StackUpdateCheckerOptions
{
    /// <summary>
    /// Whether the background service is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// Interval between update checks in minutes
    /// </summary>
    public int CheckIntervalMinutes { get; set; } = 60;
    
    /// <summary>
    /// Whether to check for updates immediately on startup
    /// </summary>
    public bool CheckOnStartup { get; set; } = true;
    
    /// <summary>
    /// Delay before first check on startup (in seconds)
    /// </summary>
    public int DelayStartupSeconds { get; set; } = 30;
}
