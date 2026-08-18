namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Port configuration for AzerothCore services
/// </summary>
public class PortConfigDto
{
    /// <summary>
    /// Authentication server port (default: 3724)
    /// </summary>
    public int AuthServer { get; set; } = 3724;
    
    /// <summary>
    /// World server port (default: 8085)
    /// </summary>
    public int WorldServer { get; set; } = 8085;
    
    /// <summary>
    /// SOAP remote admin port (default: 7878)
    /// </summary>
    public int SoapPort { get; set; } = 7878;
}
