namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Database configuration for AzerothCore stack
/// </summary>
public class DatabaseConfigDto
{
    /// <summary>
    /// MySQL root password
    /// </summary>
    public string RootPassword { get; set; } = string.Empty;
    
    /// <summary>
    /// MySQL port (default: 3306)
    /// </summary>
    public int Port { get; set; } = 3306;
}
