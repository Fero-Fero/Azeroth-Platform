namespace AzerothPlatform.Core.Contracts;

/// <summary>A server .conf file exposed for editing.</summary>
public sealed class ServerConfigFileDto
{
    /// <summary>Path relative to the stack's env/dist/etc directory (forward slashes).</summary>
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime ModifiedAt { get; set; }

    /// <summary>Grouping for the UI: "modules" for files under <c>modules/</c>, otherwise "server".</summary>
    public string Category { get; set; } = "server";
}

/// <summary>The editable server configuration files for a stack.</summary>
public sealed class ServerConfigListDto
{
    public string StackId { get; set; } = string.Empty;

    /// <summary>
    /// False when the config directory has not been populated yet (the stack must be started once
    /// so the container seeds worldserver.conf / authserver.conf from the .dist references).
    /// </summary>
    public bool Generated { get; set; }

    public List<ServerConfigFileDto> Files { get; set; } = new();
}

/// <summary>Contents of a single server .conf file.</summary>
public sealed class ServerConfigContentDto
{
    public string Path { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}
