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
    /// Compiling each selected module against the core (pre-Docker gate)
    /// </summary>
    CheckingModules,

    /// <summary>
    /// Per-module compile succeeded; Docker image build has not started yet
    /// </summary>
    ModuleCheckPassed,
    
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
