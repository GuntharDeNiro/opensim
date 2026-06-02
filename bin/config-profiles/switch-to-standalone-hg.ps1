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
$StorageTemplate = Join-Path $ProfileRoot "standalone-hg\config-include\storage\SQLiteStandalone.ini"
$StorageTarget = Join-Path $BinRoot "config-include\storage\SQLiteStandalone.ini"
$StandaloneDatabaseDir = Join-Path $BinRoot "StandaloneHG"
$StandaloneCurrencyDir = Join-Path $StandaloneDatabaseDir "Currency"
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

if (-not (Test-Path $StorageTemplate)) {
    throw "Cannot find standalone storage profile $StorageTemplate."
}

New-Item -ItemType Directory -Force -Path $StandaloneDatabaseDir | Out-Null
New-Item -ItemType Directory -Force -Path $StandaloneCurrencyDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $StorageTarget) | Out-Null
Backup-File $StorageTarget
Copy-Item -Force $StorageTemplate $StorageTarget

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
Write-Host "Switched SQLite and currency storage to dedicated bin\StandaloneHG files."
Write-Host "Hypergrid address:"
if ($HostName.Trim().Length -gt 0) {
    Write-Host "  http://$($HostName.Trim()):9000/"
} else {
    Write-Host "  http://CHANGE_ME_PUBLIC_HOST:9000/"
}
