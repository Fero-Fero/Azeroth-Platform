using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

public sealed class ModuleInstallSession : IModuleInstallSession, IDisposable
{
    private readonly bool _keepOnFailure;
    private bool _failed;
    private bool _disposed;

    public ModuleInstallSession(string rootPath, bool keepOnFailure = false)
    {
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
        _keepOnFailure = keepOnFailure;
    }

    public string RootPath { get; }
    public SessionBaseDbc? BaseDbc { get; private set; }

    public string ModuleDir(string moduleId)
    {
        var dir = Path.Combine(RootPath, Sanitize(moduleId));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void SetBaseDbc(SessionBaseDbc value)
    {
        if (BaseDbc is not null)
        {
            throw new InvalidOperationException(
                $"{value.ModuleId} cannot SetAsBaseDBC(\"{value.TableName}\") because {BaseDbc.ModuleId} already set {BaseDbc.TableName}.dbc as the base.");
        }

        BaseDbc = value;
    }

    public void MarkFailed() => _failed = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BaseDbc = null;
        if (!Directory.Exists(RootPath) || (_failed && _keepOnFailure))
        {
            return;
        }

        foreach (var moduleDir in Directory.EnumerateDirectories(RootPath))
        {
            TryDelete(Path.Combine(moduleDir, "extracted"));
        }
    }

    internal static string Sanitize(string moduleId)
    {
        if (moduleId.Contains("..", StringComparison.Ordinal) || moduleId.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException($"Invalid module id: {moduleId}");
        }

        return moduleId;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    public static string CreateRoot(string dataDir) =>
        Path.Combine(dataDir, ".module-install", Guid.NewGuid().ToString("N"));
}

public static class CsvNormalizer
{
    public static string EnsureTrailingCrlf(string text)
    {
        text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        if (!text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            text += "\r\n";
        }

        return text;
    }

    public static async Task WriteCrlfAsync(string path, string text, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            path,
            EnsureTrailingCrlf(text),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    public static string TableFileName(string tableName) => $"{NormalizeTableName(tableName)}.txt";

    public static string NormalizeTableName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".db2", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = Path.GetFileNameWithoutExtension(trimmed);
        }

        return trimmed;
    }

    public static string FirstCsvField(string line)
    {
        var trimmed = line.TrimEnd('\r', '\n');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed[0] == '"')
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 0 ? trimmed[1..end] : trimmed.Trim('"');
        }

        var comma = trimmed.IndexOf(',');
        return comma < 0 ? trimmed : trimmed[..comma];
    }
}
