namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Build phase status
/// </summary>
public enum BuildPhase
{
    /// <summary>
    /// Cloning repositories from GitHub
    /// </summary>
    Cloning,
    
    /// <summary>
    /// Preparing and integrating modules
    /// </summary>
    PreparingModules,
    
    /// <summary>
    /// Building Docker images (main compilation phase)
    /// </summary>
    Building,
    
    /// <summary>
    /// Creating final Docker images
    /// </summary>
    CreatingImages,
    
    /// <summary>
    /// Build completed successfully
    /// </summary>
    Completed,
    
    /// <summary>
    /// Build failed with errors
    /// </summary>
    Failed
}
