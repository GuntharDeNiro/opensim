param(
    [switch]$Overwrite
)

$ErrorActionPreference = "Stop"

$ProfileRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinRoot = Split-Path -Parent $ProfileRoot
$ProfileDir = Join-Path $ProfileRoot "osgrid"
$Source = Join-Path $BinRoot "OpenSim.ini"
$Target = Join-Path $ProfileDir "OpenSim.ini"
$StorageSource = Join-Path $BinRoot "config-include\storage\SQLiteStandalone.ini"
$StorageTarget = Join-Path $ProfileDir "config-include\storage\SQLiteStandalone.ini"

if (-not (Test-Path $Source)) {
    throw "Cannot find $Source. Run this from a bin folder that already has a working OpenSim.ini."
}

if ((Test-Path $Target) -and -not $Overwrite) {
    throw "$Target already exists. Use -Overwrite if you want to replace the captured OSGrid profile."
}

New-Item -ItemType Directory -Force -Path $ProfileDir | Out-Null
Copy-Item -Force $Source $Target

if (Test-Path $StorageSource) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $StorageTarget) | Out-Null
    Copy-Item -Force $StorageSource $StorageTarget
    Write-Host "Captured current SQLite standalone storage profile into:"
    Write-Host "  $StorageTarget"
}

Write-Host "Captured current OSGrid OpenSim.ini into:"
Write-Host "  $Target"
Write-Host "Switch back later with:"
Write-Host "  powershell -ExecutionPolicy Bypass -File .\config-profiles\switch-to-osgrid.ps1"
