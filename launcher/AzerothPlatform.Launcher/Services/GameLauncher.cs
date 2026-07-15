using System.Diagnostics;

namespace AzerothPlatform.Launcher.Services;

/// <summary>Starts the game executable from the install directory.</summary>
public static class GameLauncher
{
    public static void Launch(string installDirectory, string executable, string arguments)
    {
        var normalized = executable.Replace('/', Path.DirectorySeparatorChar);
        var exePath = Path.Combine(installDirectory, normalized);

        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"Game executable not found: {exePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            WorkingDirectory = installDirectory,
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }
}
