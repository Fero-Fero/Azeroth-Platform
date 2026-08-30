namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Represents a stack discovered from filesystem and Docker that can be imported
/// </summary>
public class DiscoveredStackDto
{
    /// <summary>
    /// Unique stack identifier (from directory name and Docker Compose project)
    /// </summary>
    public string StackId { get; set; } = string.Empty;
    
    /// <summary>
    /// Suggested name for import (e.g., "Imported Stack 31da1293")
    /// </summary>
    public string SuggestedName { get; set; } = string.Empty;
    
    /// <summary>
    /// Inferred server type based on git repository URL
    /// </summary>
    public ServerType InferredServerType { get; set; }
    
    /// <summary>
    /// Current status based on container states
    /// </summary>
    public StackStatus CurrentStatus { get; set; }
    
    /// <summary>
    /// MySQL database port
    /// </summary>
    public int DatabasePort { get; set; }
    
    /// <summary>
    /// Authentication server port
    /// </summary>
    public int AuthServerPort { get; set; }
    
    /// <summary>
    /// World server port
    /// </summary>
    public int WorldServerPort { get; set; }
    
    /// <summary>
    /// SOAP remote admin port
    /// </summary>
    public int SoapPort { get; set; }
    
    /// <summary>
    /// True if directory exists but no Docker containers found
    /// </summary>
    public bool IsOrphaned { get; set; }
    
    /// <summary>
    /// List of Docker container names for this stack
    /// </summary>
    public List<string> ContainerNames { get; set; } = new();
    
    /// <summary>
    /// Git repository URL for AzerothCore
    /// </summary>
    public string? CoreRepositoryUrl { get; set; }
    
    /// <summary>
    /// Current git branch
    /// </summary>
    public string? CoreBranch { get; set; }
    
    /// <summary>
    /// Current git commit SHA
    /// </summary>
    public string? CoreCommitSha { get; set; }
    
    /// <summary>
    /// When this stack was discovered
    /// </summary>
    public DateTime DiscoveredAt { get; set; }
    
    /// <summary>
    /// Modules discovered in the stack's modules directory
    /// </summary>
    public List<string>? DiscoveredModules { get; set; }
    
    /// <summary>
    /// Database root password discovered from .env file
    /// </summary>
    public string? DiscoveredDatabasePassword { get; set; }
    
    /// <summary>
    /// SOAP username discovered from configuration
    /// </summary>
    public string? DiscoveredSoapUsername { get; set; }
    
    /// <summary>
    /// SOAP password discovered from configuration
    /// </summary>
    public string? DiscoveredSoapPassword { get; set; }
    
    /// <summary>
    /// Custom environment variables discovered from docker-compose.override.yml
    /// </summary>
    public Dictionary<string, string>? DiscoveredEnvVars { get; set; }
}
