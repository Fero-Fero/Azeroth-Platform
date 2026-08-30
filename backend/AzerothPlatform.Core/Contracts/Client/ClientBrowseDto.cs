namespace AzerothPlatform.Core.Contracts;

/// <summary>A single entry (file or sub-directory) within the base client directory tree.</summary>
public sealed class ClientBrowseEntryDto
{
    /// <summary>File or directory name (no path).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>True when this entry is a sub-directory.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>File size in bytes (0 for directories).</summary>
    public long Size { get; set; }

    /// <summary>Number of immediate children (directories only; 0 for files).</summary>
    public int ItemCount { get; set; }

    /// <summary>Path relative to the base client root, using '/' separators (e.g. <c>Data/enUS</c>).</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>True when this entry is visible but cannot be deleted from the file browser.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Shown on the lock icon when <see cref="IsLocked"/> is true.</summary>
    public string? LockReason { get; set; }
}

/// <summary>Listing of a single directory level within the base client tree, for the admin file browser.</summary>
public sealed class ClientBrowseResultDto
{
    /// <summary>The listed directory's path relative to the base root ('' = root), using '/' separators.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>True when the requested directory exists inside the base client.</summary>
    public bool Exists { get; set; }

    /// <summary>Entries in the directory: sub-directories first, then files, each sorted by name.</summary>
    public List<ClientBrowseEntryDto> Entries { get; set; } = new();
}
