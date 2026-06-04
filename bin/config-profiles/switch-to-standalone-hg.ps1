param(
    [string]$HostName = "",
    [switch]$InstallFreshRegions,
    [switch]$AttachPublicGrids
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
        $content = $content.Replace("CHANGE_ME_PUBLIC_HOST", $HostName.Trim().ToLowerInvariant())
    }
    return $content
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

if (-not (Test-Path $Template)) {
    throw "Cannot find standalone profile template $Template."
}

$openSimIni = Expand-Template $Template
$openSimIni = Set-IniKey $openSimIni "Map" "GenerateMaptiles" "true"
$openSimIni = Set-IniKey $openSimIni "Map" "MapImageModule" '"Warp3DImageModule"'
$openSimIni = Set-IniKey $openSimIni "ClientStack.LindenUDP" "ViewerSimulatorVersionOverride" '"Vanilla Sim"'
$openSimIni = Set-IniKey $openSimIni "Weather" "Enabled" "true"
$openSimIni = Set-IniKey $openSimIni "Weather" "AllowDisabled" "false"
$openSimIni = Set-IniKey $openSimIni "Weather" "AutoCycleEnabled" "true"
$openSimIni = Set-IniKey $openSimIni "RegionWeb" "Enabled" "true"
$openSimIni = Set-IniKey $openSimIni "RegionWeb" "EstateTitle" '"Vanilla Sim"'
$openSimIni = Set-IniKey $openSimIni "Const" "GridName" '"Vanilla Sim"'
$openSimIni = Set-IniKey $openSimIni "Const" "GridNick" '"vanilla"'
$openSimIni = Set-IniKey $openSimIni "GridInfo" "GridName" '"Vanilla Sim"'
$openSimIni = Set-IniKey $openSimIni "GridInfo" "GridNick" '"vanilla"'
$openSimIni = Set-IniKey $openSimIni "GridInfo" "gridname" '"Vanilla Sim"'
$openSimIni = Set-IniKey $openSimIni "GridInfo" "gridnick" '"vanilla"'
$openSimIni = Set-IniKey $openSimIni "GridInfoService" "gridname" '"Vanilla Sim"'
$openSimIni = Set-IniKey $openSimIni "GridInfoService" "gridnick" '"vanilla"'
$openSimIni = Set-IniKey $openSimIni "GridInfoService" "welcome" '"${Const|BaseURL}:${Const|PublicPort}/regionweb"'
$openSimIni = Set-IniKey $openSimIni "GridInfoService" "about" '"${Const|BaseURL}:${Const|PublicPort}/regionweb"'
$openSimIni = Set-IniKey $openSimIni "GridInfoService" "help" '"${Const|BaseURL}:${Const|PublicPort}/regionweb"'
$openSimIni = Set-IniKey $openSimIni "GridInfoService" "register" '"${Const|BaseURL}:${Const|PublicPort}/regionweb"'
$openSimIni = Set-IniKey $openSimIni "GridInfoService" "economy" '"${Const|BaseURL}:${Const|PublicPort}/regionweb"'
$openSimIni = Set-IniKey $openSimIni "Groups" "Enabled" "true"
$openSimIni = Set-IniKey $openSimIni "GroupAutoInvite" "Enabled" "true"
$openSimIni = Set-IniKey $openSimIni "GroupAutoInvite" "GroupID" "65abdd0d-b201-4333-a607-d034d49407b4"
$openSimIni = Set-IniKey $openSimIni "GroupAutoInvite" "GroupName" ""
$openSimIni = Set-IniKey $openSimIni "TextBuild" "Enabled" "true"
$openSimIni = Set-IniKey $openSimIni "YEngine" "Enabled" "true"
if ($AttachPublicGrids) {
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachments" "Enabled" "true"
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachments" "Grids" '"osgrid,neverworld,zetasim"'
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachments" "AutoCreateInboundPresence" "true"
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachment.osgrid" "Enabled" "true"
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachment.osgrid" "GridServerURI" '"http://grid.osgrid.org"'
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachment.osgrid" "GridPostURI" '"http://grid.osgrid.org/grid"'
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachment.neverworld" "Enabled" "true"
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachment.neverworld" "GridServerURI" '"http://hg.neverworldgrid.com:8003"'
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachment.neverworld" "GridPostURI" '"http://hg.neverworldgrid.com:8003/grid"'
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachment.zetasim" "Enabled" "true"
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachment.zetasim" "GridServerURI" '"http://robust.zetaworlds.com:8003"'
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachment.zetasim" "GridPostURI" '"http://robust.zetaworlds.com:8003/grid"'
}

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
Write-Host "Vanilla Sim branding forced on: gridname, gridnick, RegionWeb title and viewer simulator version."
Write-Host "Showroom startup modules forced on: Warp3D maptiles, Weather /89, RegionWeb, Groups, GroupAutoInvite, TextBuild /88, YEngine."
if ($AttachPublicGrids) {
    Write-Host "Enabled secondary region attachments: OSGrid, Neverworld Grid, ZetaWorlds."
    Write-Host "OSGrid region registration uses http://grid.osgrid.org/grid, not the public hg.osgrid.org gatekeeper."
    Write-Host "Neverworld region registration uses http://hg.neverworldgrid.com:8003/grid, not the public 8002 login endpoint."
    Write-Host "ZetaWorlds region registration uses http://robust.zetaworlds.com:8003/grid, not the public hg.zetaworlds.com gatekeeper."
    Write-Host "Enabled inbound MultiGrid presence fallback for teleports from attached grid maps."
}
Write-Host "Hypergrid address:"
if ($HostName.Trim().Length -gt 0) {
    Write-Host "  http://$($HostName.Trim().ToLowerInvariant()):9000/"
} else {
    Write-Host "  http://CHANGE_ME_PUBLIC_HOST:9000/"
}
