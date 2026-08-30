using IOPath = System.IO.Path;

namespace AzerothPlatform.Infrastructure.Services.Shared;

/// <summary>
/// A scratch file or directory that deletes itself when disposed. Every temporary path the manager
/// creates in the OS temp directory goes through here so that <see cref="Root"/> is the one place
/// scratch can accumulate, and <see cref="TempWorkspaceSweeper"/> has a single tree to collect from
/// when a <c>finally</c> never got the chance to run.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    /// <summary>The only OS-temp directory the manager writes to.</summary>
    public static string Root { get; } = IOPath.Combine(IOPath.GetTempPath(), "azeroth-platform");

    private readonly bool _isDirectory;
    private bool _disposed;

    private TempWorkspace(string path, bool isDirectory)
    {
        Path = path;
        _isDirectory = isDirectory;
    }

    /// <summary>Absolute path to the scratch file or directory. Valid until disposal.</summary>
    public string Path { get; }

    /// <summary>
    /// Creates an empty directory. <paramref name="prefix"/> only labels it for anyone reading a
    /// process listing or a stale-scratch report; uniqueness comes from the appended GUID.
    /// </summary>
    public static TempWorkspace CreateDirectory(string prefix)
    {
        var path = Reserve(prefix, extension: string.Empty);
        Directory.CreateDirectory(path);
        return new TempWorkspace(path, isDirectory: true);
    }

    /// <summary>
    /// Creates an empty file, matching <see cref="IOPath.GetTempFileName"/> in that the file exists
    /// on return, so callers may open it for append or hand it to a tool that expects a target.
    /// </summary>
    public static TempWorkspace CreateFile(string prefix, string extension = ".tmp")
    {
        var path = Reserve(prefix, extension);
        File.Create(path).Dispose();
        return new TempWorkspace(path, isDirectory: false);
    }

    /// <summary>A path inside this workspace. Removed with it, so it needs no cleanup of its own.</summary>
    public string Combine(params string[] parts) => IOPath.Combine([Path, .. parts]);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryDelete(Path, _isDirectory);
    }

    private static string Reserve(string prefix, string extension)
    {
        Directory.CreateDirectory(Root);
        return IOPath.Combine(Root, $"{prefix}-{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Best-effort removal. A virus scanner or an editor holding a handle can lose us the race on
    /// Windows, so back off briefly and retry; whatever still survives is the sweeper's problem.
    /// </summary>
    internal static bool TryDelete(string path, bool isDirectory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (isDirectory)
                {
                    if (!Directory.Exists(path))
                    {
                        return true;
                    }

                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    if (!File.Exists(path))
                    {
                        return true;
                    }

                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        return false;
    }
}
