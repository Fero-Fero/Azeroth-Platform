#Requires -Version 5.1
<#
.SYNOPSIS
  Opens the Azeroth Platform manager in the default browser.

.DESCRIPTION
  Uses https://localhost/admin unless SITE_ADDRESS in .env is a non-localhost host, then
  https://{SITE_ADDRESS}/admin. The OS default browser handles the URL.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$ScriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ScriptDir)) {
    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$RepoRoot = Split-Path -Parent $ScriptDir

function Get-SiteAddress {
    $envFile = Join-Path $RepoRoot '.env'
    if (-not (Test-Path -LiteralPath $envFile)) {
        return $null
    }

    foreach ($line in Get-Content -LiteralPath $envFile) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) {
            continue
        }

        if ($trimmed -notmatch '^\s*SITE_ADDRESS\s*=\s*(.*)$') {
            continue
        }

        $value = $Matches[1].Trim()
        $hash = $value.IndexOf('#')
        if ($hash -ge 0) {
            $value = $value.Substring(0, $hash).Trim()
        }

        $value = $value.Trim('"').Trim("'")
        if ([string]::IsNullOrWhiteSpace($value)) {
            return $null
        }

        return $value
    }

    return $null
}

function Get-ManagerUrl {
    $site = Get-SiteAddress
    if ([string]::IsNullOrWhiteSpace($site) -or $site -eq 'localhost') {
        return 'https://localhost/admin'
    }

    if ($site -match '^https?://') {
        return $site.TrimEnd('/') + '/admin'
    }

    return "https://$site/admin"
}

$url = Get-ManagerUrl
Write-Host "Opening $url" -ForegroundColor Cyan
Write-Host "Accept the self-signed certificate warning if the browser shows one."
Write-Host "If the page will not load, try http://127.0.0.1:8080/admin"

Start-Process $url
