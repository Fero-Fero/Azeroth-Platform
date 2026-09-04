#Requires -Version 5.1
<#
.SYNOPSIS
  Updates this folder to the latest GitHub main branch.

.DESCRIPTION
  Fetches origin, checks out main, and fast-forwards to origin/main.
  Git for Windows is optional: if git.exe is missing, the script downloads portable MinGit
  into .tools\mingit (no installer, not added to the system PATH).
  A ZIP extract with no .git folder is turned into a clone of the GitHub repo on first run.

  Does not rebuild Docker images; run restart-platform.bat after this if you want the new code running.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$OriginUrl = 'https://github.com/Fero-Fero/AzerothPlatform.git'
$GitHubApiHeaders = @{ 'User-Agent' = 'AzerothPlatform-update' }

$ScriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ScriptDir)) {
    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$RepoRoot = Split-Path -Parent $ScriptDir
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

function Get-SystemGit {
    $cmd = Get-Command git -ErrorAction SilentlyContinue
    if ($cmd -and (Test-Path -LiteralPath $cmd.Source)) {
        return $cmd.Source
    }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'),
        (Join-Path $env:ProgramFiles 'Git\bin\git.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\cmd\git.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe')
    )
    foreach ($path in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path)) {
            return $path
        }
    }

    return $null
}

function Get-MinGitDownloadUrl {
    $arch = $env:PROCESSOR_ARCHITECTURE
    $release = Invoke-RestMethod `
        -Uri 'https://api.github.com/repos/git-for-windows/git/releases/latest' `
        -Headers $GitHubApiHeaders

    $assets = @($release.assets)
    if ($arch -eq 'ARM64') {
        $asset = $assets |
            Where-Object { $_.name -match '^MinGit-.+-arm64\.zip$' } |
            Select-Object -First 1
    }
    else {
        $asset = $assets |
            Where-Object { $_.name -match '^MinGit-.+-64-bit\.zip$' -and $_.name -notmatch 'busybox' } |
            Select-Object -First 1
    }

    if (-not $asset -or [string]::IsNullOrWhiteSpace($asset.browser_download_url)) {
        throw "Could not find a MinGit zip in the latest Git for Windows release."
    }

    return [string]$asset.browser_download_url
}

function Install-PortableGit {
    $dest = Join-Path $RepoRoot '.tools\mingit'
    $gitExe = Join-Path $dest 'cmd\git.exe'
    if (Test-Path -LiteralPath $gitExe) {
        Write-Ok "Using portable Git already downloaded to .tools\mingit."
        return $gitExe
    }

    Write-Step "Git is not installed. Downloading portable MinGit (no installer)"
    New-Item -ItemType Directory -Path (Join-Path $RepoRoot '.tools') -Force | Out-Null

    $url = Get-MinGitDownloadUrl
    Write-Host "    $url"
    $zip = Join-Path $env:TEMP 'azeroth-platform-mingit.zip'
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -Headers $GitHubApiHeaders

    if (Test-Path -LiteralPath $dest) {
        Remove-Item -LiteralPath $dest -Recurse -Force
    }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $dest -Force
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path -LiteralPath $gitExe)) {
        throw "MinGit downloaded but cmd\git.exe was not found in $dest."
    }

    Write-Ok "Portable Git is ready. It stays in this folder and is not installed system-wide."
    return $gitExe
}

function Get-GitExe {
    $system = Get-SystemGit
    if ($system) {
        return $system
    }

    return Install-PortableGit
}

function Add-GitToProcessPath {
    param([string]$GitExe)

    $cmdDir = Split-Path -Parent $GitExe
    $mingwBin = Join-Path (Split-Path -Parent $cmdDir) 'mingw64\bin'
    foreach ($dir in @($cmdDir, $mingwBin)) {
        if ((Test-Path -LiteralPath $dir) -and ($env:Path -notlike "*$dir*")) {
            $env:Path = "$dir;$env:Path"
        }
    }
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GitExe,
        [Parameter(Mandatory = $true)]
        [string[]]$GitArgs,
        [string]$FailureMessage
    )

    & $GitExe @GitArgs
    if ($LASTEXITCODE -ne 0) {
        if ([string]::IsNullOrWhiteSpace($FailureMessage)) {
            throw "git $($GitArgs -join ' ') failed with exit code $LASTEXITCODE."
        }
        throw $FailureMessage
    }
}

function Test-GitRemoteExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GitExe,
        [string]$Name = 'origin'
    )

    & $GitExe remote get-url $Name 1>$null 2>$null
    return ($LASTEXITCODE -eq 0)
}

$composeFile = Join-Path $RepoRoot 'docker-compose.yml'
if (-not (Test-Path -LiteralPath $composeFile)) {
    throw "docker-compose.yml not found. Run this script from the Azeroth Platform folder (or double-click update-platform.bat there)."
}

Write-Host "Azeroth Platform update" -ForegroundColor Cyan
Write-Host "Repo: $RepoRoot"
Write-Host ""

$git = Get-GitExe
Add-GitToProcessPath -GitExe $git
Write-Ok "Using Git: $git"

$gitDir = Join-Path $RepoRoot '.git'
$attachedRepo = $false
if (-not (Test-Path -LiteralPath $gitDir)) {
    Write-Step "This folder is not a Git clone yet (typical after Download ZIP)"
    Invoke-Git -GitExe $git -GitArgs @('init') -FailureMessage 'git init failed.'
    Invoke-Git -GitExe $git -GitArgs @('remote', 'add', 'origin', $OriginUrl) `
        -FailureMessage "Could not add origin $OriginUrl."
    $attachedRepo = $true
    Write-Ok "Connected this folder to $OriginUrl"
}
elseif (-not (Test-GitRemoteExists -GitExe $git)) {
    Write-Step "Adding GitHub as origin"
    Invoke-Git -GitExe $git -GitArgs @('remote', 'add', 'origin', $OriginUrl) `
        -FailureMessage "Could not add origin $OriginUrl."
    Write-Ok "origin -> $OriginUrl"
}

Write-Step "Fetching origin"
Invoke-Git -GitExe $git -GitArgs @('fetch', 'origin') -FailureMessage 'git fetch origin failed. Check your internet connection and try again.'

& $git rev-parse --verify origin/main 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "origin/main was not found. Check that this folder tracks GitHub and that the default branch is main."
}

if ($attachedRepo) {
    Write-Step "Checking out main"
    Invoke-Git -GitExe $git -GitArgs @('checkout', '-f', '-B', 'main', 'origin/main') `
        -FailureMessage 'Could not check out origin/main into this folder.'
}
else {
    Write-Step "Checking out main"
    Invoke-Git -GitExe $git -GitArgs @('checkout', 'main') `
        -FailureMessage 'Could not check out main. Commit or stash local changes, then run update-platform.bat again.'

    Write-Step "Fast-forwarding to origin/main"
    Invoke-Git -GitExe $git -GitArgs @('pull', '--ff-only', 'origin', 'main') `
        -FailureMessage 'git pull --ff-only origin main failed. Local commits may have diverged; resolve that in git, then retry.'
}

$head = (& $git rev-parse --short HEAD).Trim()
Write-Host ""
Write-Host "Repository is on main at $head." -ForegroundColor Green
Write-Host "Run restart-platform.bat to rebuild and start the updated platform."
