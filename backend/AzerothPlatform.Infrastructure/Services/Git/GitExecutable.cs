using System.Diagnostics;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Locates the git executable the same way <c>update-platform.ps1</c> does: PATH, Git for Windows,
/// then portable MinGit at <c>.tools/mingit/cmd/git.exe</c>.
/// </summary>
internal static class GitExecutable
{
    private static readonly Lazy<string> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string FileName => Resolved.Value;

    public static void EnsureResolved() => _ = FileName;

    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        startInfo.FileName = FileName;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // GitHub HTTP/2 git-upload-pack from the manager returns 401; git then asks for a username.
        ApplyCliOverrides(startInfo);
        PrependMinGitDirectories(startInfo);
    }

    private static readonly string[] CliOverrides =
        ["-c", "protocol.version=1", "-c", "http.version=HTTP/1.1"];

    private static void ApplyCliOverrides(ProcessStartInfo startInfo)
    {
        if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            startInfo.Arguments = string.Join(' ', CliOverrides) + " " + startInfo.Arguments;
            return;
        }

        foreach (var arg in CliOverrides)
        {
            startInfo.ArgumentList.Add(arg);
        }
    }

    private static string Resolve()
    {
        var path = Locate();
        PrependMinGitToProcessPath(path);
        return path;
    }

    private static string Locate()
    {
        var fromEnv = Environment.GetEnvironmentVariable("GIT_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var fromPath = FindOnPath("git") ?? FindOnPath("git.exe");
        if (fromPath is not null)
        {
            return fromPath;
        }

        foreach (var candidate in WindowsInstallCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var portable = FindPortableMinGit();
        if (portable is not null)
        {
            return portable;
        }

        return OperatingSystem.IsWindows() ? "git.exe" : "git";
    }

    private static string? FindPortableMinGit()
    {
        var relative = Path.Combine(".tools", "mingit", "cmd", OperatingSystem.IsWindows() ? "git.exe" : "git");
        foreach (var root in SearchRoots())
        {
            var dir = root;
            for (var i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, relative);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchRoots()
    {
        yield return Environment.CurrentDirectory;
        yield return AppContext.BaseDirectory;
    }

    private static IEnumerable<string> WindowsInstallCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
        yield return Path.Combine(programFiles, "Git", "bin", "git.exe");
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Git", "cmd", "git.exe");
        }

        yield return Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            if (OperatingSystem.IsWindows()
                && !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate + ".exe"))
            {
                return Path.GetFullPath(candidate + ".exe");
            }
        }

        return null;
    }

    private static bool IsPortableMinGit(string gitPath)
    {
        if (string.IsNullOrWhiteSpace(gitPath) || gitPath is "git" or "git.exe")
        {
            return false;
        }

        var normalized = gitPath.Replace('\\', '/');
        return normalized.Contains("/.tools/mingit/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> MinGitPathDirectories(string gitPath)
    {
        if (!IsPortableMinGit(gitPath))
        {
            yield break;
        }

        var cmdDir = Path.GetDirectoryName(gitPath);
        if (string.IsNullOrEmpty(cmdDir))
        {
            yield break;
        }

        yield return cmdDir;
        var mingitRoot = Path.GetDirectoryName(cmdDir);
        if (string.IsNullOrEmpty(mingitRoot))
        {
            yield break;
        }

        foreach (var extra in new[] { Path.Combine(mingitRoot, "mingw64", "bin"), Path.Combine(mingitRoot, "usr", "bin") })
        {
            if (Directory.Exists(extra))
            {
                yield return extra;
            }
        }
    }

    private static void PrependMinGitDirectories(ProcessStartInfo startInfo)
    {
        var extras = MinGitPathDirectories(FileName).ToList();
        if (extras.Count == 0)
        {
            return;
        }

        var key = OperatingSystem.IsWindows() ? "Path" : "PATH";
        startInfo.Environment.TryGetValue(key, out var existing);
        existing ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment[key] = string.Join(Path.PathSeparator, extras.Append(existing));
    }

    private static void PrependMinGitToProcessPath(string gitPath)
    {
        var extras = MinGitPathDirectories(gitPath).ToList();
        if (extras.Count == 0)
        {
            return;
        }

        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in extras)
        {
            if (current.Contains(dir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            current = dir + Path.PathSeparator + current;
        }

        Environment.SetEnvironmentVariable("PATH", current);
    }
}
