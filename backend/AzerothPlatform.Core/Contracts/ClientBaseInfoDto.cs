namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Summary of the shared base WoW client the admin has uploaded (the seed that every stack's client
/// container serves as its read-only base layer). Surfaced on the global Client admin tab.
/// </summary>
public sealed class ClientBaseInfoDto
{
    /// <summary>True when a base client has been uploaded (the base <c>game/</c> directory exists and is non-empty).</summary>
    public bool Exists { get; set; }

    /// <summary>Number of files in the base client.</summary>
    public int FileCount { get; set; }

    /// <summary>Total size of the base client in bytes.</summary>
    public long TotalSize { get; set; }

    /// <summary>True when <c>Wow.exe</c> is present at the base root (a basic sanity check).</summary>
    public bool HasWowExe { get; set; }

    /// <summary>True when at least one <c>Data/*.MPQ</c> exists (the client's data archives).</summary>
    public bool HasDataMpq { get; set; }

    /// <summary>Absolute path of the base client's <c>game/</c> directory on the manager host.</summary>
    public string GamePath { get; set; } = string.Empty;
}
