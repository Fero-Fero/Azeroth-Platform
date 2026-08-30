#Requires -Version 5.1
<#
.SYNOPSIS
  Installs Docker Desktop (if needed) and builds Azeroth Platform in Docker.

.DESCRIPTION
  Windows helper for Express / local setup.
    - If Docker Desktop is already installed, the script reports that, pings the platform if it is
      running, and exits. Use restart-platform.bat to rebuild.
    - If Docker is missing, it installs Docker Desktop, then builds and starts the platform.
    - If Windows still needs WSL 2, you may be asked to reboot and run this script again.

.PARAMETER SkipDockerInstall
  Do not download or install Docker Desktop. Fail if Docker is not already working.

.PARAMETER WaitSeconds
  How long to wait for the Docker engine after starting Docker Desktop. Default: 480.
#>
[CmdletBinding()]
param(
    [switch]$SkipDockerInstall,
    [int]$WaitSeconds = 480
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$RepoRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Set-Location -LiteralPath $RepoRoot

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

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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
        if ($SkipDockerInstall) { $arg += ' -SkipDockerInstall' }
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
        Write-Host "Restart this PC, then run install-platform.bat again." -ForegroundColor Yellow
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
Open Docker Desktop from the Start menu, wait until the whale icon is idle, then run install-platform.bat again.
If this is the first install, Windows may also ask you to log off once so your account can use Docker.
"@
}

function Ensure-EnvFile {
    $envPath = Join-Path $RepoRoot '.env'
    $example = Join-Path $RepoRoot '.env.example'
    if (Test-Path -LiteralPath $envPath) {
        Write-Ok ".env already exists."
        return
    }
    if (-not (Test-Path -LiteralPath $example)) {
        throw "Missing .env.example in $RepoRoot"
    }
    Copy-Item -LiteralPath $example -Destination $envPath
    Write-Ok "Created .env from .env.example (set ADMIN_PASSWORD before sharing this machine)."
}

function Test-PlatformHealth {
    try {
        $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/api/health' -UseBasicParsing -TimeoutSec 3
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300)
    }
    catch {
        return $false
    }
}

function Show-AlreadyInstalledAndExit {
    Write-Ok "Docker Desktop is already installed."

    if (Test-DockerReady) {
        if (Test-PlatformHealth) {
            Write-Ok "Platform is already running. Open https://localhost/admin"
            Write-Host "    http://127.0.0.1:8080/admin also works if HTTPS is blocked."
        }
        else {
            Write-Warn "Docker is running, but the platform did not answer yet."
            Write-Host "    Use restart-platform.bat to build and start it."
        }
    }
    else {
        Write-Warn "Docker Desktop is installed, but the engine is not running."
        Write-Host "    Open Docker Desktop, wait until it is idle, then use restart-platform.bat."
    }

    Write-Host ""
    Write-Host "Installer finished (already installed)." -ForegroundColor Green
    exit 0
}

function Start-Platform {
    Write-Step "Building and starting Azeroth Platform"
    Write-Host "    First build downloads Node, .NET, and Docker images. This often takes 10-20 minutes."

    Add-DockerToPath
    $docker = Get-DockerCli
    if (-not $docker) {
        throw "docker.exe was not found on PATH after installing Docker Desktop."
    }

    Push-Location -LiteralPath $RepoRoot
    try {
        & $docker compose up -d --build
        if ($LASTEXITCODE -ne 0) {
            throw "docker compose up failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

# --- main ---

Write-Host "Azeroth Platform Windows installer" -ForegroundColor Cyan
Write-Host "Repo: $RepoRoot"

$composeFile = Join-Path $RepoRoot 'docker-compose.yml'
if (-not (Test-Path -LiteralPath $composeFile)) {
    throw "docker-compose.yml not found. Run this script from the Azeroth Platform repo (or double-click install-platform.bat there)."
}

Add-DockerToPath

if ((Get-DockerDesktopExe) -or (Get-DockerCli)) {
    Show-AlreadyInstalledAndExit
}

if ($SkipDockerInstall) {
    throw "Docker is not installed. Re-run without -SkipDockerInstall."
}

if (-not (Test-WslPresent)) {
    Enable-WslFeatures
}
else {
    Write-Ok "WSL is available."
}

Install-DockerDesktop
Start-DockerEngine -TimeoutSeconds $WaitSeconds
Ensure-EnvFile
Start-Platform

Write-Host ""
Write-Host "Platform is up." -ForegroundColor Green
Write-Host "Open https://localhost/admin  (accept the self-signed certificate warning)."
Write-Host "Log in with ADMIN_PASSWORD from .env, then Create Stack and pick Express Setup for a local realm."
Write-Host "Dashboard is also at http://127.0.0.1:8080/admin if HTTPS is blocked."
Write-Host ""
Write-Host "Useful later:"
Write-Host "  docker compose logs -f"
Write-Host "  docker compose down"
Write-Host "  docker compose up -d --build"
