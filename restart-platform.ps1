#Requires -Version 5.1
<#
.SYNOPSIS
  Rebuilds and starts Azeroth Platform with Docker Compose.

.DESCRIPTION
  Runs `docker compose up -d --build` from the repository root. Docker Desktop must already be
  installed and running (use install-platform.bat if it is not).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Set-Location -LiteralPath $RepoRoot

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

$composeFile = Join-Path $RepoRoot 'docker-compose.yml'
if (-not (Test-Path -LiteralPath $composeFile)) {
    throw "docker-compose.yml not found. Run this script from the Azeroth Platform repo (or double-click restart-platform.bat there)."
}

Add-DockerToPath
$docker = Get-DockerCli
if (-not $docker) {
    throw "docker.exe was not found. Install Docker Desktop with install-platform.bat, then try again."
}

& $docker info --format '{{.ServerVersion}}' 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "Docker is installed but the engine is not running. Open Docker Desktop, wait until it is idle, then run restart-platform.bat again."
}

Write-Host "Azeroth Platform restart" -ForegroundColor Cyan
Write-Host "Repo: $RepoRoot"
Write-Host ""
Write-Host "==> docker compose up -d --build" -ForegroundColor Cyan
Write-Host "    Rebuilding images can take several minutes."

& $docker compose up -d --build
if ($LASTEXITCODE -ne 0) {
    throw "docker compose up failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Platform rebuilt and started." -ForegroundColor Green
Write-Host "Open https://localhost/admin  (accept the self-signed certificate warning)."
Write-Host "Dashboard is also at http://127.0.0.1:8080/admin if HTTPS is blocked."
