using IOPath = System.IO.Path;

namespace AzerothPlatform.Tests.TestSupport;

/// <summary>
/// A scratch directory for one test, removed when the test finishes. Use it with <c>using</c> (or a
/// field on an <see cref="IDisposable"/> test class) so a failing assertion cannot leave the tree
/// behind - the suite creates hundreds of these per run.
/// </summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string prefix = "azp-test")
    {
        Path = IOPath.Combine(IOPath.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => IOPath.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A held handle is not worth failing a passing test over; the sweeper collects it later.
        }
    }
}
