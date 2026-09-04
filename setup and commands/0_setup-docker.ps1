#Requires -Version 5.1
<#
.SYNOPSIS
  Checks this OS, verifies Windows virtualization, then installs Docker if it is missing.

.DESCRIPTION
  Windows: firmware virtualization must already be on (a script cannot flip that BIOS switch).
  If it is on, Docker Desktop is used when docker is missing, and the engine is started.
  Linux and macOS: run 0_setup-docker.sh instead.

.PARAMETER WaitSeconds
  How long to wait for the Docker engine after starting Docker Desktop. Default: 480.
#>
[CmdletBinding()]
param(
    [int]$WaitSeconds = 480
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ScriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ScriptDir)) {
    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$RepoRoot = Split-Path -Parent $ScriptDir

$VirtGuide = 'https://support.microsoft.com/windows/enable-virtualization-on-windows-c5578302-6e43-4b4b-a449-8ced115f58e1'

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Yellow
}

function Test-IsWindowsOs {
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        return [bool]$IsWindows
    }
    return $env:OS -eq 'Windows_NT'
}

function Get-OsName {
    if (Test-IsWindowsOs) {
        try {
            $caption = (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).Caption
            if (-not [string]::IsNullOrWhiteSpace($caption)) {
                return $caption.Trim()
            }
        }
        catch {
            # CIM can fail in constrained sessions; fall back to the Windows version string.
        }
        return "Windows $([System.Environment]::OSVersion.Version)"
    }

    if ($PSVersionTable.PSVersion.Major -ge 6 -and $IsMacOS) {
        return 'macOS'
    }
    if ($PSVersionTable.PSVersion.Major -ge 6 -and $IsLinux) {
        return 'Linux'
    }
    return [System.Environment]::OSVersion.Platform.ToString()
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-WindowsVirtualizationEnabled {
    try {
        $system = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
        if ($system.HypervisorPresent) {
            return $true
        }
    }
    catch {
        # Fall through to firmware and systeminfo checks.
    }

    try {
        $cpu = Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop | Select-Object -First 1
        if ($null -ne $cpu.VirtualizationFirmwareEnabled) {
            return [bool]$cpu.VirtualizationFirmwareEnabled
        }
    }
    catch {
        # Fall through to systeminfo.
    }

    $info = & systeminfo.exe 2>$null | Out-String
    if ($info -match 'A hypervisor has been detected') {
        return $true
    }
    if ($info -match 'Virtualization Enabled In Firmware:\s+Yes') {
        return $true
    }
    if ($info -match 'Virtualization Enabled In Firmware:\s+No') {
        return $false
    }

    throw "Could not determine whether virtualization is enabled. Open Task Manager (Ctrl+Shift+Esc) → Performance → CPU and check Virtualization."
}

function Show-VirtualizationDisabledAndExit {
    Write-Host ""
    Write-Host "Virtualization is disabled on this Windows machine." -ForegroundColor Red
    Write-Host "Docker cannot run until it is turned on in BIOS/UEFI. A script cannot do that."
    Write-Host ""
    Write-Host "  1. Open Task Manager (Ctrl+Shift+Esc) → Performance → CPU."
    Write-Host "  2. If Virtualization says Disabled, reboot into BIOS/UEFI and enable VT-x / AMD-V / SVM."
    Write-Host "  3. Save, boot Windows, then run 0_setup-docker.bat again."
    Write-Host ""
    Write-Host "Microsoft walkthrough (per PC brand):" -ForegroundColor Yellow
    Write-Host "  $VirtGuide"
    exit 1
}

function Get-DockerDesktopExe {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\Docker Desktop.exe')
    )
    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }
    return $null
}

function Get-DockerCli {
    $cmd = Get-Command docker -ErrorAction SilentlyContinue
    if ($cmd -and (Test-Path -LiteralPath $cmd.Source)) {
        return $cmd.Source
    }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe')
    )
    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }
    return $null
}

function Add-DockerToPath {
    $dirs = @(
        (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin'),
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin')
    )
    foreach ($dir in $dirs) {
        if ((Test-Path -LiteralPath $dir) -and ($env:Path -notlike "*$dir*")) {
            $env:Path = "$dir;$env:Path"
        }
    }
}

function Test-DockerReady {
    Add-DockerToPath
    $docker = Get-DockerCli
    if (-not $docker) {
        return $false
    }

    & $docker info --format '{{.ServerVersion}}' 1>$null 2>$null
    return ($LASTEXITCODE -eq 0)
}

function Test-WslPresent {
    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if (-not $wsl) {
        return $false
    }

    & wsl.exe --status 1>$null 2>$null
    return ($LASTEXITCODE -eq 0)
}

function Enable-WslFeatures {
    Write-Step "Enabling WSL 2 (required by Docker Desktop)"

    if (-not (Test-IsAdmin)) {
        Write-Warn "Administrator approval is needed once to turn on WSL 2."
        $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        if ($WaitSeconds -ne 480) { $arg += " -WaitSeconds $WaitSeconds" }
        $elevated = Start-Process `
            -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
            -Verb RunAs `
            -ArgumentList $arg `
            -Wait `
            -PassThru
        exit $elevated.ExitCode
    }

    $restartNeeded = $false
    foreach ($feature in @('Microsoft-Windows-Subsystem-Linux', 'VirtualMachinePlatform')) {
        $state = Get-WindowsOptionalFeature -Online -FeatureName $feature
        if ($state.State -ne 'Enabled') {
            Write-Host "    Enabling $feature..."
            $result = Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart
            if ($result.RestartNeeded) {
                $restartNeeded = $true
            }
        }
        else {
            Write-Ok "$feature is already on."
        }
    }

    & wsl.exe --set-default-version 2 1>$null 2>$null
    try {
        & wsl.exe --update
    }
    catch {
        Write-Warn "WSL kernel update skipped: $($_.Exception.Message)"
    }

    if ($restartNeeded) {
        Write-Host ""
        Write-Host "Windows needs a restart to finish enabling WSL 2." -ForegroundColor Yellow
        Write-Host "Restart this PC, then run 0_setup-docker.bat again." -ForegroundColor Yellow
        exit 2
    }
}

function Install-DockerDesktop {
    Write-Step "Installing the latest Docker Desktop"

    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'amd64' }
    $url = "https://desktop.docker.com/win/main/$arch/Docker%20Desktop%20Installer.exe"
    $installer = Join-Path $env:TEMP 'DockerDesktopInstaller.exe'

    Write-Host "    Downloading Docker Desktop ($arch)..."
    Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing

    try {
        $installArgs = @('install', '--quiet', '--accept-license', '--backend=wsl-2')
        if (-not (Test-IsAdmin)) {
            $installArgs += '--user'
            Write-Host "    Installing for this Windows user (no admin required)..."
        }
        else {
            Write-Host "    Installing for all users..."
        }

        $proc = Start-Process -FilePath $installer -ArgumentList $installArgs -Wait -PassThru
        if ($proc.ExitCode -ne 0) {
            throw "Docker Desktop installer exited with code $($proc.ExitCode)."
        }
    }
    finally {
        Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
    }

    Add-DockerToPath
    Write-Ok "Docker Desktop installed."
}

function Start-DockerEngine {
    param([int]$TimeoutSeconds)

    Add-DockerToPath
    if (Test-DockerReady) {
        Write-Ok "Docker engine is already running."
        return
    }

    Write-Step "Starting Docker Desktop"
    $desktop = Get-DockerDesktopExe
    if (-not $desktop) {
        throw "Docker Desktop is installed but Docker Desktop.exe was not found. Open Docker Desktop from the Start menu, wait until it is running, then run this script again."
    }

    Start-Process -FilePath $desktop | Out-Null
    Write-Host "    Waiting up to $TimeoutSeconds seconds for the engine..."

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-DockerReady) {
            Write-Ok "Docker engine is ready."
            return
        }
        Start-Sleep -Seconds 5
    }

    throw @"
Docker Desktop is installed but the engine did not become ready in time.
Open Docker Desktop from the Start menu, wait until the whale icon is idle, then run 0_setup-docker.bat again.
If this is the first install, Windows may also ask you to log off once so your account can use Docker.
"@
}

# --- main ---

Write-Host "Azeroth Platform Docker setup" -ForegroundColor Cyan
$osName = Get-OsName
Write-Host "Operating system: $osName"

if (-not (Test-IsWindowsOs)) {
    $sh = Join-Path $ScriptDir '0_setup-docker.sh'
    Write-Host ""
    Write-Host "This PowerShell helper installs Docker Desktop on Windows." -ForegroundColor Yellow
    Write-Host "On Linux or macOS run:"
    if (Test-Path -LiteralPath $sh) {
        Write-Host "  bash `"$sh`""
    }
    else {
        Write-Host "  ./0_setup-docker.sh"
    }
    exit 1
}

Write-Step "Checking virtualization"
if (-not (Test-WindowsVirtualizationEnabled)) {
    Show-VirtualizationDisabledAndExit
}
Write-Ok "Virtualization is enabled."

Add-DockerToPath
if (Test-DockerReady) {
    $docker = Get-DockerCli
    Write-Ok "Docker is already installed and the engine is running ($docker)."
    Write-Host ""
    Write-Host "Docker is ready." -ForegroundColor Green
    exit 0
}

if ((Get-DockerDesktopExe) -or (Get-DockerCli)) {
    Write-Ok "Docker is installed; starting the engine."
    Start-DockerEngine -TimeoutSeconds $WaitSeconds
    Write-Host ""
    Write-Host "Docker is ready." -ForegroundColor Green
    exit 0
}

Write-Warn "Docker is not installed."

if (-not (Test-WslPresent)) {
    Enable-WslFeatures
}
else {
    Write-Ok "WSL is available."
}

Install-DockerDesktop
Start-DockerEngine -TimeoutSeconds $WaitSeconds

Write-Host ""
Write-Host "Docker is ready." -ForegroundColor Green
Write-Host "You can now run 1_install-platform.bat in this folder to build Azeroth Platform."
