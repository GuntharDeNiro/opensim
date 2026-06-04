$ErrorActionPreference = "Stop"

$ProfileRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinRoot = Split-Path -Parent $ProfileRoot
$BackupDir = Join-Path $ProfileRoot "backups"
$Source = Join-Path $ProfileRoot "osgrid\OpenSim.ini"
$Target = Join-Path $BinRoot "OpenSim.ini"
$StorageSource = Join-Path $ProfileRoot "osgrid\config-include\storage\SQLiteStandalone.ini"
$StorageTarget = Join-Path $BinRoot "config-include\storage\SQLiteStandalone.ini"

function Backup-File([string]$Path) {
    if (-not (Test-Path $Path)) {
        return
    }

    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $name = Split-Path -Leaf $Path
    Copy-Item -Force $Path (Join-Path $BackupDir "$name.$stamp.bak")
}

function Set-IniKey([string]$Content, [string]$Section, [string]$Key, [string]$Value) {
    $newline = "`n"
    if ($Content.Contains("`r`n")) {
        $newline = "`r`n"
    }

    $lines = $Content -split "`r?`n", -1
    $inSection = $false
    $sectionStart = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if ($line -match '^\s*\[(.+)\]\s*$') {
            if ($inSection) {
                $before = $lines[0..($i - 1)]
                $after = $lines[$i..($lines.Count - 1)]
                return [string]::Join($newline, @($before + "    $Key = $Value" + $after))
            }

            $inSection = ($matches[1] -eq $Section)
            if ($inSection) {
                $sectionStart = $i
            }
            continue
        }

        if ($inSection -and $line -match "^\s*$([regex]::Escape($Key))\s*=") {
            $indent = ""
            if ($line -match '^(\s*)') {
                $indent = $matches[1]
            }

            $lines[$i] = "$indent$Key = $Value"
            return [string]::Join($newline, $lines)
        }
    }

    if ($inSection -and $sectionStart -ge 0) {
        return [string]::Join($newline, @($lines + "    $Key = $Value"))
    }

    return [string]::Join($newline, @($lines + "" + "[$Section]" + "    $Key = $Value"))
}

if (-not (Test-Path $Source)) {
    throw "No captured OSGrid profile found at $Source. Run capture-osgrid-profile.ps1 while your OSGrid config is active."
}

$openSimIni = Get-Content -Raw $Source
$openSimIni = Set-IniKey $openSimIni "Map" "GenerateMaptiles" "true"
$openSimIni = Set-IniKey $openSimIni "Map" "MapImageModule" '"Warp3DImageModule"'
$openSimIni = Set-IniKey $openSimIni "Weather" "Enabled" "true"
$openSimIni = Set-IniKey $openSimIni "Weather" "AllowDisabled" "false"
$openSimIni = Set-IniKey $openSimIni "Weather" "AutoCycleEnabled" "true"
$openSimIni = Set-IniKey $openSimIni "RegionWeb" "Enabled" "true"
$openSimIni = Set-IniKey $openSimIni "TextBuild" "Enabled" "true"
$openSimIni = Set-IniKey $openSimIni "YEngine" "Enabled" "true"

Backup-File $Target
Set-Content -Encoding UTF8 -Path $Target -Value $openSimIni

if (Test-Path $StorageSource) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $StorageTarget) | Out-Null
    Backup-File $StorageTarget
    Copy-Item -Force $StorageSource $StorageTarget
    Write-Host "Restored captured OSGrid SQLite standalone storage profile."
}

Write-Host "Switched OpenSim.ini to the captured OSGrid profile."
Write-Host "Showroom startup modules forced on: Warp3D maptiles, Weather /89, RegionWeb, TextBuild /88, YEngine."
Write-Host "Regions were not touched."
