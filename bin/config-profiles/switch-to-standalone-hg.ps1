param(
    [string]$HostName = "",
    [switch]$InstallFreshRegions
)

$ErrorActionPreference = "Stop"

$ProfileRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinRoot = Split-Path -Parent $ProfileRoot
$BackupDir = Join-Path $ProfileRoot "backups"
$Template = Join-Path $ProfileRoot "standalone-hg\OpenSim.ini"
$Target = Join-Path $BinRoot "OpenSim.ini"
$RegionTemplate = Join-Path $ProfileRoot "standalone-hg\Regions\Regions.ini"
$RegionTargetDir = Join-Path $BinRoot "Regions"
$RegionTarget = Join-Path $RegionTargetDir "Regions.ini"

function Backup-File([string]$Path) {
    if (-not (Test-Path $Path)) {
        return
    }

    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $name = Split-Path -Leaf $Path
    Copy-Item -Force $Path (Join-Path $BackupDir "$name.$stamp.bak")
}

function Expand-Template([string]$Path) {
    $content = Get-Content -Raw $Path
    if ($HostName.Trim().Length -gt 0) {
        $content = $content.Replace("CHANGE_ME_PUBLIC_HOST", $HostName.Trim())
    }
    return $content
}

if (-not (Test-Path $Template)) {
    throw "Cannot find standalone profile template $Template."
}

$openSimIni = Expand-Template $Template
if ($openSimIni.Contains("CHANGE_ME_PUBLIC_HOST")) {
    Write-Warning "OpenSim.ini still contains CHANGE_ME_PUBLIC_HOST. Pass -HostName with your public IP or DNS."
}

Backup-File $Target
Set-Content -Encoding UTF8 -Path $Target -Value $openSimIni

if ($InstallFreshRegions) {
    if (-not (Test-Path $RegionTemplate)) {
        throw "Cannot find sample region template $RegionTemplate."
    }

    $regionsIni = Expand-Template $RegionTemplate
    $regionsIni = $regionsIni.Replace("CHANGE_ME_REGION_UUID", [guid]::NewGuid().ToString())

    New-Item -ItemType Directory -Force -Path $RegionTargetDir | Out-Null
    Backup-File $RegionTarget
    Set-Content -Encoding UTF8 -Path $RegionTarget -Value $regionsIni

    Write-Host "Installed sample standalone Regions.ini."
} else {
    Write-Host "Regions were not touched. Existing OSGrid/lab regions remain in place."
}

Write-Host "Switched OpenSim.ini to standalone Hypergrid."
Write-Host "Hypergrid address:"
if ($HostName.Trim().Length -gt 0) {
    Write-Host "  http://$($HostName.Trim()):9000/"
} else {
    Write-Host "  http://CHANGE_ME_PUBLIC_HOST:9000/"
}
