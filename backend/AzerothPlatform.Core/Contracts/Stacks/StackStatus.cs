namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Stack operational status
/// </summary>
public enum StackStatus
{
    /// <summary>
    /// Stack is currently building
    /// </summary>
    Building,
    
    /// <summary>
    /// Stack is deployed but containers are stopped
    /// </summary>
    Stopped,
    
    /// <summary>
    /// Stack is performing first-time initialization (db-import, client-data download)
    /// </summary>
    Initializing,
    
    /// <summary>
    /// Stack is starting up
    /// </summary>
    Starting,
    
    /// <summary>
    /// Stack is partially operational (some required services down, e.g., worldserver crash-looping)
    /// </summary>
    Degraded,
    
    /// <summary>
    /// Stack is running normally (all required services healthy)
    /// </summary>
    Running,
    
    /// <summary>
    /// Stack has failed or is in error state
    /// </summary>
    Failed,

    /// <summary>
    /// Cloud VPC exists but the create-stack wizard has not finished (no worldserver build yet).
    /// </summary>
    SetupIncomplete
}
