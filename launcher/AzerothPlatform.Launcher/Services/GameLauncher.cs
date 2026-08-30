using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace AzerothPlatform.Launcher.Services;

/// <summary>Starts the game executable from the install directory.</summary>
public static class GameLauncher
{
    public const string ElevatedStartScriptName = "Start WoW.cmd";

    public static void Launch(string installDirectory, string executable, string arguments)
    {
        var installDir = Path.GetFullPath(installDirectory);
        var normalized = executable.Replace('/', Path.DirectorySeparatorChar);
        var exePath = Path.GetFullPath(Path.Combine(installDir, normalized));

        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"Game executable not found: {exePath}");
        }

        // Wow.exe resolves Data/ from the process working directory (the folder that contains the
        // exe). An elevated start often defaults cwd to System32, so letter patches such as
        // patch-W never load. Play must never inherit the launcher's admin token.
        var exeDir = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException($"Could not resolve directory for {exePath}");
        WriteElevatedStartScript(exeDir, Path.GetFileName(exePath));

        if (OperatingSystem.IsWindows() && IsProcessElevated()
            && TryStartUnelevated(exePath, arguments ?? string.Empty, exeDir))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = exeDir,
            UseShellExecute = false,
        };

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the game.");
    }

    /// <summary>
    /// Shortcut for launching from Explorer as admin: <c>cd /d</c> to this folder before starting Wow.
    /// </summary>
    internal static void WriteElevatedStartScript(string exeDir, string exeFileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.WriteAllText(
                Path.Combine(exeDir, ElevatedStartScriptName),
                $"""
                @echo off
                cd /d "%~dp0"
                start "" /D "%~dp0" "{exeFileName}" %*
                """);
        }
        catch
        {
            // Best-effort: Play still starts Wow.exe with WorkingDirectory set.
        }
    }

    internal static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Starts Wow with the unelevated Explorer token so Data\ is the install folder, not System32.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryStartUnelevated(string exePath, string arguments, string workingDirectory)
    {
        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(shellWindow, out var shellPid);
        if (shellPid == 0)
        {
            return false;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, shellPid);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(process, TokenDuplicate | TokenQuery, out var processToken)
                || processToken == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (!DuplicateTokenEx(
                        processToken,
                        TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId,
                        IntPtr.Zero,
                        SecurityImpersonation,
                        TokenPrimary,
                        out var primaryToken)
                    || primaryToken == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    var commandLine = new StringBuilder();
                    commandLine.Append('"').Append(exePath).Append('"');
                    if (!string.IsNullOrEmpty(arguments))
                    {
                        commandLine.Append(' ').Append(arguments);
                    }

                    var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>() };
                    if (!CreateProcessWithTokenW(
                            primaryToken,
                            0,
                            exePath,
                            commandLine,
                            0,
                            IntPtr.Zero,
                            workingDirectory,
                            ref startup,
                            out var info))
                    {
                        return false;
                    }

                    if (info.Process != IntPtr.Zero)
                    {
                        CloseHandle(info.Process);
                    }

                    if (info.Thread != IntPtr.Zero)
                    {
                        CloseHandle(info.Thread);
                    }

                    return true;
                }
                finally
                {
                    CloseHandle(primaryToken);
                }
            }
            finally
            {
                CloseHandle(processToken);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Ptr;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        uint ImpersonationLevel,
        int TokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr hToken,
        uint dwLogonFlags,
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
