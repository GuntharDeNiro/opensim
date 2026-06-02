$ErrorActionPreference = "Stop"

$ProfileRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinRoot = Split-Path -Parent $ProfileRoot
$BackupDir = Join-Path $ProfileRoot "backups"
$Source = Join-Path $ProfileRoot "osgrid\OpenSim.ini"
$Target = Join-Path $BinRoot "OpenSim.ini"

function Backup-File([string]$Path) {
    if (-not (Test-Path $Path)) {
        return
    }

    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $name = Split-Path -Leaf $Path
    Copy-Item -Force $Path (Join-Path $BackupDir "$name.$stamp.bak")
}

if (-not (Test-Path $Source)) {
    throw "No captured OSGrid profile found at $Source. Run capture-osgrid-profile.ps1 while your OSGrid config is active."
}

Backup-File $Target
Copy-Item -Force $Source $Target

Write-Host "Switched OpenSim.ini to the captured OSGrid profile."
Write-Host "Regions were not touched."
