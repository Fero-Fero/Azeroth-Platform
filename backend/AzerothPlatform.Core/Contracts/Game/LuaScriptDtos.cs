namespace AzerothPlatform.Core.Contracts;

/// <summary>A single file (or folder) in a stack's Lua script tree.</summary>
public sealed class LuaScriptFileDto
{
    /// <summary>Path relative to the stack's lua_scripts root, using forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }
}

/// <summary>The Lua scripts served to a stack's worldserver (via Eluna).</summary>
public sealed class LuaScriptListDto
{
    public string StackId { get; set; } = string.Empty;

    /// <summary>
    /// Whether an Eluna module is part of this stack's build. Lua scripts only run when Eluna is
    /// compiled into the worldserver, so the UI warns when this is false.
    /// </summary>
    public bool ElunaPresent { get; set; }

    public List<LuaScriptFileDto> Files { get; set; } = new();

    public long TotalSize { get; set; }
}

/// <summary>Contents of a single Lua script file.</summary>
public sealed class LuaScriptContentDto
{
    public string Path { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}
