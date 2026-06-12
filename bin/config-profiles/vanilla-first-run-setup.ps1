param(
    [int]$Port = 9090
)

$ErrorActionPreference = "Stop"

$ProfileRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinRoot = Split-Path -Parent $ProfileRoot
$SetupDir = Join-Path $BinRoot "VanillaFirstRun"
$SwitchScript = Join-Path $ProfileRoot "switch-to-standalone-hg.ps1"
$Prefix = "http://127.0.0.1:$Port/"

$script:SetupState = @{
    Status = "idle"
    Settings = $null
    RemoteAdminPassword = ""
    OpenSimProcess = $null
    FirstUserReady = $false
    FirstUserMessage = ""
    StartedAt = $null
    LoginUri = ""
    RegionWebUrl = ""
    PublicRegionWebUrl = ""
    Error = ""
}

function Html([string]$Value) {
    if ($null -eq $Value) {
        return ""
    }

    return [System.Net.WebUtility]::HtmlEncode($Value)
}

function Quote-Ini([string]$Value) {
    if ($null -eq $Value) {
        $Value = ""
    }

    return '"' + ($Value.Replace('"', '')) + '"'
}

function Bool-Text([bool]$Value) {
    if ($Value) {
        return "true"
    }

    return "false"
}

function Xml-Escape([string]$Value) {
    if ($null -eq $Value) {
        return ""
    }

    return [System.Security.SecurityElement]::Escape($Value)
}

function New-XmlRpcStruct([string]$MethodName, [hashtable]$Values) {
    $members = New-Object System.Text.StringBuilder
    foreach ($key in $Values.Keys) {
        $value = $Values[$key]
        if ($value -is [int]) {
            [void]$members.Append("<member><name>$(Xml-Escape $key)</name><value><int>$value</int></value></member>")
        } elseif ($value -is [bool]) {
            $boolValue = 0
            if ($value) {
                $boolValue = 1
            }
            [void]$members.Append("<member><name>$(Xml-Escape $key)</name><value><boolean>$boolValue</boolean></value></member>")
        } else {
            [void]$members.Append("<member><name>$(Xml-Escape $key)</name><value><string>$(Xml-Escape ([string]$value))</string></value></member>")
        }
    }

    return "<?xml version=`"1.0`"?><methodCall><methodName>$(Xml-Escape $MethodName)</methodName><params><param><value><struct>$members</struct></value></param></params></methodCall>"
}

function Test-XmlRpcSuccess([string]$Content) {
    return $Content -match "<name>success</name>\s*<value>\s*<(boolean|string|int)>(1|true|True)</"
}

function Parse-Form($Request) {
    $form = @{}

    if ($Request.HttpMethod -ne "POST") {
        return $form
    }

    $reader = New-Object System.IO.StreamReader($Request.InputStream, $Request.ContentEncoding)
    $body = $reader.ReadToEnd()

    foreach ($pair in ($body -split "&")) {
        if ([string]::IsNullOrWhiteSpace($pair)) {
            continue
        }

        $parts = $pair -split "=", 2
        $name = [System.Uri]::UnescapeDataString($parts[0].Replace("+", " "))
        $value = ""
        if ($parts.Length -gt 1) {
            $value = [System.Uri]::UnescapeDataString($parts[1].Replace("+", " "))
        }

        $form[$name] = $value
    }

    return $form
}

function Get-FormValue($Form, [string]$Name, [string]$Default) {
    if ($Form -and $Form.ContainsKey($Name)) {
        return [string]$Form[$Name]
    }

    return $Default
}

function Get-FormBool($Form, [string]$Name, [bool]$Default) {
    if ($Form -and $Form.ContainsKey("__submitted")) {
        return $Form.ContainsKey($Name)
    }

    return $Default
}

function Get-SafeInt($Form, [string]$Name, [int]$Default, [int]$Min, [int]$Max) {
    $raw = Get-FormValue $Form $Name ([string]$Default)
    $parsed = 0
    if (-not [int]::TryParse($raw, [ref]$parsed)) {
        return $Default
    }

    if ($parsed -lt $Min) {
        return $Min
    }

    if ($parsed -gt $Max) {
        return $Max
    }

    return $parsed
}

function Normalize-Host([string]$HostName) {
    $hostValue = $HostName.Trim().ToLowerInvariant()
    $hostValue = $hostValue -replace '^https?://', ''
    $hostValue = ($hostValue -split '/')[0]

    if ($hostValue.Contains(":")) {
        $hostValue = ($hostValue -split ':')[0]
    }

    if ([string]::IsNullOrWhiteSpace($hostValue)) {
        throw "Write a public DNS name or IP for the grid, for example vanilla-sim.com."
    }

    return $hostValue
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

function Read-Settings($Form) {
    $coordsRaw = Get-FormValue $Form "region_coords" "10000,10000"
    if ($coordsRaw -notmatch '^\s*(\d+)\s*,\s*(\d+)\s*$') {
        throw "Region coordinates must look like 10000,10000."
    }

    $regionX = [int]$matches[1]
    $regionY = [int]$matches[2]
    $publicPort = Get-SafeInt $Form "public_port" 9000 1025 65000
    $regionPort = Get-SafeInt $Form "region_port" 9000 1025 65000

    $firstName = (Get-FormValue $Form "avatar_first" "Estate").Trim()
    $lastName = (Get-FormValue $Form "avatar_last" "Owner").Trim()
    $avatarPassword = Get-FormValue $Form "avatar_password" ""

    if ($firstName -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{1,31}$') {
        throw "Avatar first name should be 2-32 letters, numbers, underscores or dashes."
    }

    if ($lastName -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{1,31}$') {
        throw "Avatar last name should be 2-32 letters, numbers, underscores or dashes."
    }

    if ($avatarPassword.Length -lt 6) {
        throw "Choose an avatar password with at least 6 characters."
    }

    $gridName = (Get-FormValue $Form "grid_name" "Vanilla Sim").Trim()
    $gridNick = (Get-FormValue $Form "grid_nick" "vanilla").Trim().ToLowerInvariant()
    $regionName = (Get-FormValue $Form "region_name" "Vanilla Welcome").Trim()

    if ([string]::IsNullOrWhiteSpace($gridName)) {
        throw "Grid name cannot be empty."
    }

    if ([string]::IsNullOrWhiteSpace($gridNick)) {
        throw "Grid short name cannot be empty."
    }

    if ([string]::IsNullOrWhiteSpace($regionName)) {
        throw "Region name cannot be empty."
    }

    return @{
        HostName = Normalize-Host (Get-FormValue $Form "host_name" "vanilla-sim.com")
        PublicPort = $publicPort
        GridName = $gridName
        GridNick = $gridNick
        RegionName = $regionName.Replace("[", "").Replace("]", "")
        RegionX = $regionX
        RegionY = $regionY
        RegionPort = $regionPort
        MaxPrims = Get-SafeInt $Form "max_prims" 30000 1000 1000000
        MaxAgents = Get-SafeInt $Form "max_agents" 80 1 1000
        AvatarFirst = $firstName
        AvatarLast = $lastName
        AvatarPassword = $avatarPassword
        AvatarEmail = (Get-FormValue $Form "avatar_email" "").Trim()
        FeatureMaps = Get-FormBool $Form "feature_maps" $true
        FeatureRegionWeb = Get-FormBool $Form "feature_regionweb" $true
        FeatureWeather = Get-FormBool $Form "feature_weather" $true
        FeatureCurrency = Get-FormBool $Form "feature_currency" $true
        FeatureGroups = Get-FormBool $Form "feature_groups" $true
        FeatureTextBuild = Get-FormBool $Form "feature_textbuild" $true
        FeatureMultiGrid = Get-FormBool $Form "feature_multigrid" $true
        FeatureScripts = Get-FormBool $Form "feature_scripts" $true
        FeaturePhysics = Get-FormBool $Form "feature_physics" $true
        FeatureOfflineIM = Get-FormBool $Form "feature_offlineim" $true
    }
}

function Write-RegionsIni($Settings) {
    $regionsDir = Join-Path $BinRoot "Regions"
    $regionTarget = Join-Path $regionsDir "Regions.ini"
    New-Item -ItemType Directory -Force -Path $regionsDir | Out-Null

    $regionUuid = [guid]::NewGuid().ToString()
    $content = @"
;; Generated by Vanilla Sim First-Run Setup on $(Get-Date -Format "yyyy-MM-dd HH:mm:ss").
;; Re-run bin\start-vanilla-setup.bat if you want to replace this starter region.

[$($Settings.RegionName)]
RegionUUID = $regionUuid
Location = $($Settings.RegionX),$($Settings.RegionY)
InternalAddress = 0.0.0.0
InternalPort = $($Settings.RegionPort)
AllowAlternatePorts = False
ExternalHostName = $($Settings.HostName)
DefaultLanding = <128,128,30>
MaxPrims = $($Settings.MaxPrims)
MaxAgents = $($Settings.MaxAgents)
RegionType = "Vanilla Sim Showroom"
"@

    Set-Content -Encoding UTF8 -Path $regionTarget -Value $content
}

function Write-FirstRunScripts($Settings, [string]$RemoteAdminPassword) {
    New-Item -ItemType Directory -Force -Path $SetupDir | Out-Null

    $finalizePath = Join-Path $SetupDir "finalize-first-user.ps1"
    $launcherPath = Join-Path $BinRoot "start-vanilla-sim-first-run.bat"
    $summaryPath = Join-Path $SetupDir "setup-summary.txt"

    $finalize = @"
param(
    [int]`$TimeoutSeconds = 240
)

`$ErrorActionPreference = "Stop"
`$RemoteAdminUrl = "http://127.0.0.1:$($Settings.PublicPort)/"
`$RemoteAdminPassword = "$RemoteAdminPassword"
`$FirstName = "$($Settings.AvatarFirst.Replace('"', ''))"
`$LastName = "$($Settings.AvatarLast.Replace('"', ''))"
`$AvatarPassword = "$($Settings.AvatarPassword.Replace('"', ''))"
`$AvatarEmail = "$($Settings.AvatarEmail.Replace('"', ''))"
`$RegionX = $($Settings.RegionX)
`$RegionY = $($Settings.RegionY)

function Xml-Escape([string]`$Value) {
    if (`$null -eq `$Value) {
        return ""
    }

    return [System.Security.SecurityElement]::Escape(`$Value)
}

function New-XmlRpcStruct([string]`$MethodName, [hashtable]`$Values) {
    `$members = New-Object System.Text.StringBuilder
    foreach (`$key in `$Values.Keys) {
        `$value = `$Values[`$key]
        if (`$value -is [int]) {
            [void]`$members.Append("<member><name>`$(Xml-Escape `$key)</name><value><int>`$value</int></value></member>")
        } elseif (`$value -is [bool]) {
            `$boolValue = 0
            if (`$value) { `$boolValue = 1 }
            [void]`$members.Append("<member><name>`$(Xml-Escape `$key)</name><value><boolean>`$boolValue</boolean></value></member>")
        } else {
            [void]`$members.Append("<member><name>`$(Xml-Escape `$key)</name><value><string>`$(Xml-Escape ([string]`$value))</string></value></member>")
        }
    }

    return "<?xml version=`"1.0`"?><methodCall><methodName>`$(Xml-Escape `$MethodName)</methodName><params><param><value><struct>`$members</struct></value></param></params></methodCall>"
}

function Test-XmlRpcSuccess([string]`$Content) {
    return `$Content -match "<name>success</name>\s*<value>\s*<(boolean|string|int)>(1|true|True)</"
}

`$existsBody = New-XmlRpcStruct "admin_exists_user" @{
    password = `$RemoteAdminPassword
    user_firstname = `$FirstName
    user_lastname = `$LastName
}

`$createBody = New-XmlRpcStruct "admin_create_user" @{
    password = `$RemoteAdminPassword
    user_firstname = `$FirstName
    user_lastname = `$LastName
    user_password = `$AvatarPassword
    user_email = `$AvatarEmail
    start_region_x = `$RegionX
    start_region_y = `$RegionY
}

`$deadline = (Get-Date).AddSeconds(`$TimeoutSeconds)
Write-Host "Waiting for Vanilla Sim RemoteAdmin at `$RemoteAdminUrl ..."

while ((Get-Date) -lt `$deadline) {
    try {
        `$exists = Invoke-WebRequest -Uri `$RemoteAdminUrl -Method Post -ContentType "text/xml" -Body `$existsBody -UseBasicParsing
        if (Test-XmlRpcSuccess `$exists.Content) {
            Write-Host "Avatar `$FirstName `$LastName already exists. Keeping it."
            exit 0
        }

        `$created = Invoke-WebRequest -Uri `$RemoteAdminUrl -Method Post -ContentType "text/xml" -Body `$createBody -UseBasicParsing
        if (Test-XmlRpcSuccess `$created.Content) {
            Write-Host "Created avatar `$FirstName `$LastName."
            exit 0
        }

        Write-Host "RemoteAdmin answered, but the avatar was not created yet. Retrying..."
    }
    catch {
        Start-Sleep -Seconds 4
    }
}

Write-Warning "Could not create `$FirstName `$LastName before timeout. Open OpenSim console and check RemoteAdmin logs."
exit 1
"@

    Set-Content -Encoding UTF8 -Path $finalizePath -Value $finalize

    $launcher = @"
@echo off
setlocal

pushd "%~dp0" >nul

if not exist "%~dp0OpenSim.exe" (
    echo OpenSim.exe was not found in this folder.
    echo Build Vanilla Sim first, then run this file again from bin.
    popd >nul
    pause
    exit /b 1
)

echo.
echo === Starting Vanilla Sim ===
echo Login URI: http://$($Settings.HostName):$($Settings.PublicPort)/
echo First avatar: $($Settings.AvatarFirst) $($Settings.AvatarLast)
echo.

start "Vanilla Sim" "%~dp0OpenSim.exe"

echo Waiting for OpenSim startup so the first avatar can be created...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0VanillaFirstRun\finalize-first-user.ps1"
set "FINALIZE_RESULT=%ERRORLEVEL%"

echo.
if "%FINALIZE_RESULT%"=="0" (
    echo Ready. Open your viewer and log in to http://$($Settings.HostName):$($Settings.PublicPort)/
) else (
    echo Vanilla Sim started, but the first avatar automation did not finish.
    echo You can still create the avatar from the OpenSim console if needed.
)

popd >nul
pause
exit /b %FINALIZE_RESULT%
"@

    Set-Content -Encoding ASCII -Path $launcherPath -Value $launcher

    $summary = @"
Vanilla Sim First-Run Setup
===========================

Login URI: http://$($Settings.HostName):$($Settings.PublicPort)/
Grid name: $($Settings.GridName)
Grid nick: $($Settings.GridNick)
Starter region: $($Settings.RegionName) at $($Settings.RegionX),$($Settings.RegionY)
First avatar: $($Settings.AvatarFirst) $($Settings.AvatarLast)

Next step:
1. The setup wizard starts OpenSim.exe automatically when you press Create.
2. The wizard splash page waits for RegionWeb and then opens it automatically.
3. Log in from the viewer with the URI above and your first avatar credentials.

Fallback:
If the browser was closed before startup finished, run
bin\start-vanilla-sim-first-run.bat from bin. It starts OpenSim.exe and creates
or confirms the first avatar.

Security note:
This folder contains a one-time RemoteAdmin password and the first avatar
password so the bootstrap script can create the account. After the first login,
you can delete bin\VanillaFirstRun if you do not need the bootstrap helper again.
"@

    Set-Content -Encoding UTF8 -Path $summaryPath -Value $summary
}

function Start-VanillaSim($Settings) {
    $exe = Join-Path $BinRoot "OpenSim.exe"
    if (-not (Test-Path $exe)) {
        throw "OpenSim.exe was not found in bin. Build Vanilla Sim first, then run the setup wizard again."
    }

    if (Test-RegionWebReady) {
        $script:SetupState.Status = "ready"
        return
    }

    if ($script:SetupState.OpenSimProcess -and -not $script:SetupState.OpenSimProcess.HasExited) {
        return
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.WorkingDirectory = $BinRoot
    $psi.UseShellExecute = $true
    $psi.WindowStyle = "Normal"

    $script:SetupState.OpenSimProcess = [System.Diagnostics.Process]::Start($psi)
    $script:SetupState.Status = "starting"
    $script:SetupState.StartedAt = Get-Date
}

function Try-CreateFirstUser() {
    $settings = $script:SetupState.Settings
    if ($null -eq $settings) {
        return "Waiting for setup settings."
    }

    if ($script:SetupState.FirstUserReady) {
        return $script:SetupState.FirstUserMessage
    }

    $remoteAdminUrl = "http://127.0.0.1:$($settings.PublicPort)/"
    $existsBody = New-XmlRpcStruct "admin_exists_user" @{
        password = $script:SetupState.RemoteAdminPassword
        user_firstname = $settings.AvatarFirst
        user_lastname = $settings.AvatarLast
    }

    $createBody = New-XmlRpcStruct "admin_create_user" @{
        password = $script:SetupState.RemoteAdminPassword
        user_firstname = $settings.AvatarFirst
        user_lastname = $settings.AvatarLast
        user_password = $settings.AvatarPassword
        user_email = $settings.AvatarEmail
        start_region_x = $settings.RegionX
        start_region_y = $settings.RegionY
    }

    try {
        $exists = Invoke-WebRequest -Uri $remoteAdminUrl -Method Post -ContentType "text/xml" -Body $existsBody -UseBasicParsing -TimeoutSec 4
        if (Test-XmlRpcSuccess $exists.Content) {
            $script:SetupState.FirstUserReady = $true
            $script:SetupState.FirstUserMessage = "First avatar already exists."
            return $script:SetupState.FirstUserMessage
        }

        $created = Invoke-WebRequest -Uri $remoteAdminUrl -Method Post -ContentType "text/xml" -Body $createBody -UseBasicParsing -TimeoutSec 4
        if (Test-XmlRpcSuccess $created.Content) {
            $script:SetupState.FirstUserReady = $true
            $script:SetupState.FirstUserMessage = "First avatar created."
            return $script:SetupState.FirstUserMessage
        }

        return "RemoteAdmin is online; waiting for user services."
    }
    catch {
        return "Waiting for RemoteAdmin and user services."
    }
}

function Test-RegionWebReady() {
    if ([string]::IsNullOrWhiteSpace($script:SetupState.RegionWebUrl)) {
        return $false
    }

    try {
        $response = Invoke-WebRequest -Uri $script:SetupState.RegionWebUrl -Method Get -UseBasicParsing -TimeoutSec 4
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 400)
    }
    catch {
        return $false
    }
}

function Get-SetupStatus() {
    $settings = $script:SetupState.Settings

    if ($null -eq $settings) {
        return @{
            ready = $false
            phase = "Waiting for setup"
            message = "Fill the wizard form to create Vanilla Sim."
            loginUri = ""
            regionWebUrl = ""
            publicRegionWebUrl = ""
            firstUserReady = $false
            error = ""
        }
    }

    if ($script:SetupState.OpenSimProcess -and $script:SetupState.OpenSimProcess.HasExited) {
        $script:SetupState.Status = "error"
        $script:SetupState.Error = "OpenSim.exe exited before RegionWeb was ready. Check the OpenSim console window."
    }

    $firstUserMessage = Try-CreateFirstUser
    $regionWebReady = Test-RegionWebReady

    if ($regionWebReady) {
        $script:SetupState.Status = "ready"
    }

    $phase = "Starting OpenSim.exe"
    if ($script:SetupState.Status -eq "configured") {
        $phase = "Writing configuration"
    } elseif ($script:SetupState.Status -eq "starting") {
        $phase = "Booting simulator services"
    } elseif ($script:SetupState.Status -eq "ready") {
        $phase = "RegionWeb is ready"
    } elseif ($script:SetupState.Status -eq "error") {
        $phase = "Startup needs attention"
    }

    $message = "Config ready. OpenSim is starting, first avatar is being prepared, then RegionWeb will open automatically."
    if (-not [string]::IsNullOrWhiteSpace($firstUserMessage)) {
        $message = $firstUserMessage
    }

    return @{
        ready = $regionWebReady
        phase = $phase
        message = $message
        loginUri = $script:SetupState.LoginUri
        regionWebUrl = $script:SetupState.RegionWebUrl
        publicRegionWebUrl = $script:SetupState.PublicRegionWebUrl
        firstUserReady = $script:SetupState.FirstUserReady
        error = $script:SetupState.Error
    }
}

function Apply-Setup($Form) {
    if (-not (Test-Path $SwitchScript)) {
        throw "Cannot find $SwitchScript."
    }

    $settings = Read-Settings $Form

    if ($settings.FeatureMultiGrid) {
        & $SwitchScript -HostName $settings.HostName -InstallFreshRegions -AttachPublicGrids
    } else {
        & $SwitchScript -HostName $settings.HostName -InstallFreshRegions
    }

    $target = Join-Path $BinRoot "OpenSim.ini"
    $openSimIni = Get-Content -Raw $target

    $openSimIni = Set-IniKey $openSimIni "Const" "BaseHostname" (Quote-Ini $settings.HostName)
    $openSimIni = Set-IniKey $openSimIni "Const" "PublicPort" (Quote-Ini ([string]$settings.PublicPort))
    $openSimIni = Set-IniKey $openSimIni "Const" "GridName" (Quote-Ini $settings.GridName)
    $openSimIni = Set-IniKey $openSimIni "Const" "GridNick" (Quote-Ini $settings.GridNick)
    $openSimIni = Set-IniKey $openSimIni "Network" "http_listener_port" '${Const|PublicPort}'
    $openSimIni = Set-IniKey $openSimIni "GridInfo" "GridName" (Quote-Ini $settings.GridName)
    $openSimIni = Set-IniKey $openSimIni "GridInfo" "GridNick" (Quote-Ini $settings.GridNick)
    $openSimIni = Set-IniKey $openSimIni "GridInfo" "gridname" (Quote-Ini $settings.GridName)
    $openSimIni = Set-IniKey $openSimIni "GridInfo" "gridnick" (Quote-Ini $settings.GridNick)
    $openSimIni = Set-IniKey $openSimIni "GridInfoService" "gridname" (Quote-Ini $settings.GridName)
    $openSimIni = Set-IniKey $openSimIni "GridInfoService" "gridnick" (Quote-Ini $settings.GridNick)
    $openSimIni = Set-IniKey $openSimIni "RegionWeb" "EstateTitle" (Quote-Ini $settings.GridName)
    $openSimIni = Set-IniKey $openSimIni "ClientStack.LindenUDP" "ViewerSimulatorVersionOverride" (Quote-Ini $settings.GridName)

    $openSimIni = Set-IniKey $openSimIni "Map" "GenerateMaptiles" (Bool-Text $settings.FeatureMaps)
    if ($settings.FeatureMaps) {
        $openSimIni = Set-IniKey $openSimIni "Map" "MapImageModule" '"Warp3DImageModule"'
    }

    $openSimIni = Set-IniKey $openSimIni "Weather" "Enabled" (Bool-Text $settings.FeatureWeather)
    $openSimIni = Set-IniKey $openSimIni "Weather" "AllowDisabled" (Bool-Text (-not $settings.FeatureWeather))
    $openSimIni = Set-IniKey $openSimIni "Weather" "AutoCycleEnabled" (Bool-Text $settings.FeatureWeather)
    $openSimIni = Set-IniKey $openSimIni "RegionWeb" "Enabled" (Bool-Text $settings.FeatureRegionWeb)
    $openSimIni = Set-IniKey $openSimIni "RegionWeb" "CurrencyPortalEnabled" (Bool-Text $settings.FeatureCurrency)
    $openSimIni = Set-IniKey $openSimIni "RegionWeb" "CurrencyBuyEnabled" (Bool-Text $settings.FeatureCurrency)
    $openSimIni = Set-IniKey $openSimIni "RegionWeb" "CurrencyTransferEnabled" (Bool-Text $settings.FeatureCurrency)
    $openSimIni = Set-IniKey $openSimIni "Economy" "SellEnabled" (Bool-Text $settings.FeatureCurrency)
    $openSimIni = Set-IniKey $openSimIni "Groups" "Enabled" (Bool-Text $settings.FeatureGroups)
    $openSimIni = Set-IniKey $openSimIni "GroupAutoInvite" "Enabled" (Bool-Text $settings.FeatureGroups)
    $openSimIni = Set-IniKey $openSimIni "TextBuild" "Enabled" (Bool-Text $settings.FeatureTextBuild)
    $openSimIni = Set-IniKey $openSimIni "YEngine" "Enabled" (Bool-Text $settings.FeatureScripts)
    $openSimIni = Set-IniKey $openSimIni "MultiGridAttachments" "Enabled" (Bool-Text $settings.FeatureMultiGrid)
    foreach ($gridSection in @("MultiGridAttachment.osgrid", "MultiGridAttachment.neverworld", "MultiGridAttachment.zetasim", "MultiGridAttachment.craft", "MultiGridAttachment.vanilla")) {
        $openSimIni = Set-IniKey $openSimIni $gridSection "ExternalHostName" (Quote-Ini $settings.HostName)
        $openSimIni = Set-IniKey $openSimIni $gridSection "ServerURI" (Quote-Ini "http://$($settings.HostName):$($settings.PublicPort)")
    }
    $openSimIni = Set-IniKey $openSimIni "Messaging" "ForwardOfflineGroupMessages" (Bool-Text $settings.FeatureOfflineIM)

    if (-not $settings.FeatureOfflineIM) {
        $openSimIni = Set-IniKey $openSimIni "Messaging" "OfflineMessageModule" '""'
    }

    $openSimIni = Set-IniKey $openSimIni "ODEPhysicsSettings" "boat_water_dynamics_enabled" (Bool-Text $settings.FeaturePhysics)
    $openSimIni = Set-IniKey $openSimIni "ODEPhysicsSettings" "physical_prim_water_dynamics_enabled" (Bool-Text $settings.FeaturePhysics)
    $openSimIni = Set-IniKey $openSimIni "ODEPhysicsSettings" "avatar_physics_tuning_enabled" (Bool-Text $settings.FeaturePhysics)
    $openSimIni = Set-IniKey $openSimIni "ODEPhysicsSettings" "avatar_social_physics_enabled" (Bool-Text $settings.FeaturePhysics)

    $remoteAdminPassword = ([guid]::NewGuid().ToString("N") + [guid]::NewGuid().ToString("N")).Substring(0, 32)
    $openSimIni = Set-IniKey $openSimIni "RemoteAdmin" "enabled" "true"
    $openSimIni = Set-IniKey $openSimIni "RemoteAdmin" "port" "0"
    $openSimIni = Set-IniKey $openSimIni "RemoteAdmin" "bind_ip_address" '"127.0.0.1"'
    $openSimIni = Set-IniKey $openSimIni "RemoteAdmin" "access_ip_addresses" "127.0.0.1"
    $openSimIni = Set-IniKey $openSimIni "RemoteAdmin" "access_password" (Quote-Ini $remoteAdminPassword)
    $openSimIni = Set-IniKey $openSimIni "RemoteAdmin" "enabled_methods" '"admin_exists_user|admin_create_user"'

    Set-Content -Encoding UTF8 -Path $target -Value $openSimIni
    Write-RegionsIni $settings
    Write-FirstRunScripts $settings $remoteAdminPassword

    $script:SetupState.Status = "configured"
    $script:SetupState.Settings = $settings
    $script:SetupState.RemoteAdminPassword = $remoteAdminPassword
    $script:SetupState.FirstUserReady = $false
    $script:SetupState.FirstUserMessage = ""
    $script:SetupState.LoginUri = "http://$($settings.HostName):$($settings.PublicPort)/"
    $script:SetupState.RegionWebUrl = "http://127.0.0.1:$($settings.PublicPort)/regionweb"
    $script:SetupState.PublicRegionWebUrl = "http://$($settings.HostName):$($settings.PublicPort)/regionweb"
    $script:SetupState.Error = ""

    return $settings
}

function Checkbox($Form, [string]$Name, [string]$Title, [string]$Body, [bool]$Default) {
    $checked = ""
    if (Get-FormBool $Form $Name $Default) {
        $checked = "checked"
    }

    return @"
<label class="switch-card">
  <input type="checkbox" name="$Name" value="1" $checked>
  <span>
    <strong>$Title</strong>
    <small>$Body</small>
  </span>
</label>
"@
}

function Render-Page($Form, [string]$Message, [string]$MessageKind) {
    $hostName = Html (Get-FormValue $Form "host_name" "vanilla-sim.com")
    $gridName = Html (Get-FormValue $Form "grid_name" "Vanilla Sim")
    $gridNick = Html (Get-FormValue $Form "grid_nick" "vanilla")
    $publicPort = Html (Get-FormValue $Form "public_port" "9000")
    $regionName = Html (Get-FormValue $Form "region_name" "Vanilla Welcome")
    $regionCoords = Html (Get-FormValue $Form "region_coords" "10000,10000")
    $regionPort = Html (Get-FormValue $Form "region_port" "9000")
    $maxPrims = Html (Get-FormValue $Form "max_prims" "30000")
    $maxAgents = Html (Get-FormValue $Form "max_agents" "80")
    $avatarFirst = Html (Get-FormValue $Form "avatar_first" "Estate")
    $avatarLast = Html (Get-FormValue $Form "avatar_last" "Owner")
    $avatarEmail = Html (Get-FormValue $Form "avatar_email" "")
    $notice = ""

    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        $notice = "<div class=""notice $MessageKind"">$Message</div>"
    }

    $maps = Checkbox $Form "feature_maps" "Beautiful world maps" "Generate sharp Warp3D maps with water depth and object detail." $true
    $regionWeb = Checkbox $Form "feature_regionweb" "Website for your regions" "Publish a visitor-facing website, wallet, screenshots and admin tools." $true
    $weather = Checkbox $Form "feature_weather" "Live weather" "Rain, storms, snow, forecast messages and weather controls are ready at startup." $true
    $currency = Checkbox $Form "feature_currency" "Money and wallet" "Give avatars a starter balance and enable the RegionWeb wallet." $true
    $groups = Checkbox $Form "feature_groups" "Groups and welcomes" "Local HG groups, offline notices and automatic default group invite." $true
    $textBuild = Checkbox $Form "feature_textbuild" "AI-style building tool" "Estate managers can use the /88 text builder to create inworld content faster." $true
    $multiGrid = Checkbox $Form "feature_multigrid" "Attach to many grids" "Publish your regions to OSGrid, Neverworld, ZetaWorlds and Craft at the same time." $true
    $scripts = Checkbox $Form "feature_scripts" "Second Life style scripts" "Start with YEngine and LSL/OSSL scripting enabled for maximum compatibility." $true
    $physics = Checkbox $Form "feature_physics" "Realistic physics showroom" "Enable Vanilla Sim tuning for water, boats, rubber, avatars and materials." $true
    $offline = Checkbox $Form "feature_offlineim" "Offline messages" "Store offline IMs, group notices and invites locally." $true

    return @"
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Vanilla Sim First-Run Setup</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #05080d;
      --panel: #101722;
      --panel2: #151f2b;
      --text: #f7fbff;
      --muted: #a8b6c8;
      --line: #263545;
      --cyan: #14c8ff;
      --magenta: #c600ff;
      --green: #4ef0a3;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background:
        radial-gradient(circle at 20% 0%, rgba(20, 200, 255, .18), transparent 28rem),
        radial-gradient(circle at 90% 10%, rgba(198, 0, 255, .22), transparent 30rem),
        var(--bg);
      color: var(--text);
      font-family: Inter, Segoe UI, Arial, sans-serif;
      line-height: 1.5;
    }
    header {
      position: sticky;
      top: 0;
      z-index: 2;
      border-bottom: 2px solid var(--cyan);
      background: rgba(0, 0, 0, .88);
      backdrop-filter: blur(18px);
    }
    .nav {
      width: min(1180px, calc(100% - 32px));
      margin: 0 auto;
      min-height: 84px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 18px;
    }
    .brand {
      display: flex;
      align-items: center;
      gap: 14px;
      font-weight: 950;
      letter-spacing: 0;
      text-transform: uppercase;
    }
    .mark {
      width: 58px;
      height: 58px;
      border: 4px solid var(--cyan);
      border-radius: 14px;
      display: grid;
      place-items: center;
      color: var(--cyan);
      font-size: 25px;
      box-shadow: 0 0 24px rgba(20, 200, 255, .35);
      transform: rotate(-6deg);
    }
    .brand span {
      display: block;
      line-height: .86;
      font-size: 28px;
    }
    main {
      width: min(1180px, calc(100% - 32px));
      margin: 0 auto;
      padding: 54px 0 70px;
    }
    .hero {
      display: grid;
      grid-template-columns: minmax(0, 1.05fr) minmax(320px, .95fr);
      gap: 30px;
      align-items: stretch;
    }
    .hero-copy {
      padding: 34px 0;
    }
    .eyebrow {
      color: var(--cyan);
      font-weight: 900;
      letter-spacing: .16em;
      text-transform: uppercase;
      font-size: 14px;
    }
    h1 {
      margin: 14px 0 18px;
      font-size: clamp(42px, 7vw, 86px);
      line-height: .92;
      letter-spacing: 0;
    }
    .lede {
      max-width: 690px;
      color: #dce8f6;
      font-size: 22px;
      font-weight: 650;
    }
    .panel {
      background: linear-gradient(180deg, rgba(21,31,43,.98), rgba(9,13,20,.98));
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 24px;
      box-shadow: 0 24px 80px rgba(0, 0, 0, .38);
    }
    .panel h2 {
      margin: 0 0 8px;
      font-size: 26px;
    }
    .panel p {
      margin: 0;
      color: var(--muted);
      font-weight: 650;
    }
    .notice {
      margin: 28px 0;
      padding: 18px 20px;
      border-radius: 8px;
      border: 1px solid var(--line);
      background: rgba(20, 200, 255, .12);
      color: #eafdff;
      font-weight: 750;
    }
    .notice.success { background: rgba(78, 240, 163, .14); }
    .notice.error { background: rgba(255, 80, 120, .14); }
    form {
      margin-top: 34px;
      display: grid;
      gap: 18px;
    }
    fieldset {
      margin: 0;
      padding: 24px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: rgba(12, 18, 27, .92);
    }
    legend {
      padding: 0 8px;
      color: var(--cyan);
      font-weight: 950;
      text-transform: uppercase;
      letter-spacing: .12em;
      font-size: 13px;
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 16px;
    }
    label.field {
      display: grid;
      gap: 7px;
      color: var(--muted);
      font-size: 13px;
      font-weight: 800;
      text-transform: uppercase;
      letter-spacing: .08em;
    }
    input[type="text"], input[type="password"], input[type="email"], input[type="number"] {
      width: 100%;
      border: 1px solid #31455b;
      border-radius: 8px;
      background: #07101a;
      color: var(--text);
      min-height: 48px;
      padding: 0 14px;
      font-size: 16px;
      font-weight: 700;
      outline: none;
    }
    input:focus {
      border-color: var(--cyan);
      box-shadow: 0 0 0 3px rgba(20, 200, 255, .22);
    }
    .features {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
    }
    .switch-card {
      display: grid;
      grid-template-columns: 22px minmax(0, 1fr);
      gap: 12px;
      align-items: start;
      padding: 14px;
      border-radius: 8px;
      border: 1px solid var(--line);
      background: var(--panel);
    }
    .switch-card input { margin-top: 4px; accent-color: var(--magenta); }
    .switch-card strong {
      display: block;
      font-size: 16px;
      color: var(--text);
    }
    .switch-card small {
      display: block;
      margin-top: 4px;
      color: var(--muted);
      font-size: 13px;
      font-weight: 650;
    }
    .actions {
      position: sticky;
      bottom: 0;
      display: flex;
      justify-content: flex-end;
      gap: 12px;
      padding: 18px 0 0;
      background: linear-gradient(0deg, var(--bg) 72%, transparent);
    }
    button {
      min-height: 58px;
      border: 0;
      border-radius: 8px;
      padding: 0 28px;
      color: white;
      background: linear-gradient(135deg, var(--magenta), #8418ff);
      box-shadow: 0 18px 42px rgba(198, 0, 255, .35);
      font-size: 18px;
      font-weight: 950;
      cursor: pointer;
    }
    .hint {
      color: var(--muted);
      font-size: 14px;
      margin-top: 10px;
    }
    @media (max-width: 840px) {
      .hero, .grid, .features { grid-template-columns: 1fr; }
      .nav { min-height: 74px; }
    }
  </style>
</head>
<body>
  <header>
    <div class="nav">
      <div class="brand"><div class="mark">VS</div><div><span>Vanilla</span><span>Sim</span></div></div>
      <strong>First-Run Setup</strong>
    </div>
  </header>
  <main>
    <section class="hero">
      <div class="hero-copy">
        <div class="eyebrow">Fresh install wizard</div>
        <h1>Build a playable grid in minutes.</h1>
        <p class="lede">Answer a few simple questions. Vanilla Sim writes the OpenSim config, creates the first region, prepares the first avatar and enables the showroom features people should see immediately.</p>
      </div>
      <div class="panel">
        <h2>What this creates</h2>
        <p>A standalone Hypergrid with RegionWeb, wallet, groups, weather, maps, scripts, multi-grid publishing, physics tuning and a first avatar bootstrap helper.</p>
      </div>
    </section>

    $notice

    <form method="post" action="/apply">
      <input type="hidden" name="__submitted" value="1">

      <fieldset>
        <legend>Public identity</legend>
        <div class="grid">
          <label class="field">Public DNS or IP
            <input name="host_name" type="text" value="$hostName" placeholder="vanilla-sim.com" required>
          </label>
          <label class="field">Login port
            <input name="public_port" type="number" min="1025" max="65000" value="$publicPort" required>
          </label>
          <label class="field">Grid name
            <input name="grid_name" type="text" value="$gridName" required>
          </label>
          <label class="field">Short name
            <input name="grid_nick" type="text" value="$gridNick" required>
          </label>
        </div>
        <div class="hint">Use a real domain when possible. Some public grids reject raw IP Hypergrid addresses.</div>
      </fieldset>

      <fieldset>
        <legend>First region</legend>
        <div class="grid">
          <label class="field">Region name
            <input name="region_name" type="text" value="$regionName" required>
          </label>
          <label class="field">Grid coordinates
            <input name="region_coords" type="text" value="$regionCoords" required>
          </label>
          <label class="field">Region UDP port
            <input name="region_port" type="number" min="1025" max="65000" value="$regionPort" required>
          </label>
          <label class="field">Max avatars
            <input name="max_agents" type="number" min="1" max="1000" value="$maxAgents" required>
          </label>
          <label class="field">Max prims
            <input name="max_prims" type="number" min="1000" max="1000000" value="$maxPrims" required>
          </label>
        </div>
      </fieldset>

      <fieldset>
        <legend>First avatar</legend>
        <div class="grid">
          <label class="field">First name
            <input name="avatar_first" type="text" value="$avatarFirst" required>
          </label>
          <label class="field">Last name
            <input name="avatar_last" type="text" value="$avatarLast" required>
          </label>
          <label class="field">Password
            <input name="avatar_password" type="password" value="" required>
          </label>
          <label class="field">Email
            <input name="avatar_email" type="email" value="$avatarEmail">
          </label>
        </div>
      </fieldset>

      <fieldset>
        <legend>Showroom features</legend>
        <div class="features">
          $maps
          $regionWeb
          $weather
          $currency
          $groups
          $textBuild
          $multiGrid
          $scripts
          $physics
          $offline
        </div>
      </fieldset>

      <div class="actions">
        <button type="submit">Create Vanilla Sim</button>
      </div>
    </form>
  </main>
</body>
</html>
"@
}

function Render-SplashPage() {
    $loginUri = Html $script:SetupState.LoginUri
    $regionWebUrl = Html $script:SetupState.RegionWebUrl
    $publicRegionWebUrl = Html $script:SetupState.PublicRegionWebUrl

    return @"
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Starting Vanilla Sim</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #05080d;
      --panel: #101722;
      --text: #f7fbff;
      --muted: #a8b6c8;
      --line: #263545;
      --cyan: #14c8ff;
      --magenta: #c600ff;
      --green: #4ef0a3;
      --amber: #ffd166;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background:
        radial-gradient(circle at 18% 12%, rgba(20, 200, 255, .24), transparent 26rem),
        radial-gradient(circle at 84% 18%, rgba(198, 0, 255, .24), transparent 30rem),
        linear-gradient(180deg, #05080d, #09111c 70%, #05080d);
      color: var(--text);
      font-family: Inter, Segoe UI, Arial, sans-serif;
      display: grid;
      place-items: center;
      padding: 28px;
    }
    .shell {
      width: min(1060px, 100%);
      display: grid;
      grid-template-columns: minmax(0, 1fr) 360px;
      gap: 24px;
      align-items: stretch;
    }
    .hero, .side {
      border: 1px solid var(--line);
      border-radius: 8px;
      background: rgba(12, 18, 27, .92);
      box-shadow: 0 28px 90px rgba(0, 0, 0, .42);
    }
    .hero {
      min-height: 520px;
      padding: clamp(26px, 5vw, 54px);
      display: flex;
      flex-direction: column;
      justify-content: center;
      overflow: hidden;
      position: relative;
    }
    .hero::after {
      content: "";
      position: absolute;
      inset: auto -10% -22% 20%;
      height: 220px;
      background: radial-gradient(ellipse, rgba(20, 200, 255, .22), transparent 68%);
      pointer-events: none;
    }
    .brand {
      display: flex;
      align-items: center;
      gap: 14px;
      font-weight: 950;
      letter-spacing: 0;
      text-transform: uppercase;
      margin-bottom: 38px;
    }
    .mark {
      width: 62px;
      height: 62px;
      border: 4px solid var(--cyan);
      border-radius: 14px;
      display: grid;
      place-items: center;
      color: var(--cyan);
      font-size: 26px;
      box-shadow: 0 0 26px rgba(20, 200, 255, .36);
      transform: rotate(-6deg);
    }
    .brand span {
      display: block;
      line-height: .86;
      font-size: 30px;
    }
    .eyebrow {
      color: var(--cyan);
      font-weight: 950;
      letter-spacing: .16em;
      text-transform: uppercase;
      font-size: 14px;
    }
    h1 {
      margin: 14px 0 16px;
      font-size: clamp(44px, 7vw, 84px);
      line-height: .92;
      letter-spacing: 0;
    }
    .message {
      color: #dce8f6;
      font-size: 22px;
      font-weight: 700;
      max-width: 720px;
    }
    .progress {
      height: 8px;
      margin-top: 36px;
      border-radius: 999px;
      overflow: hidden;
      background: #162231;
    }
    .bar {
      width: 35%;
      height: 100%;
      border-radius: inherit;
      background: linear-gradient(90deg, var(--cyan), var(--magenta));
      animation: glide 2.6s ease-in-out infinite;
    }
    @keyframes glide {
      0% { transform: translateX(-115%); width: 32%; }
      50% { width: 62%; }
      100% { transform: translateX(330%); width: 32%; }
    }
    .side {
      padding: 24px;
      display: grid;
      gap: 16px;
      align-content: center;
    }
    .step {
      display: grid;
      grid-template-columns: 26px minmax(0, 1fr);
      gap: 12px;
      align-items: start;
      padding: 14px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: #0b131e;
    }
    .dot {
      width: 14px;
      height: 14px;
      margin-top: 4px;
      border-radius: 50%;
      background: #516074;
    }
    .step.active .dot {
      background: var(--amber);
      box-shadow: 0 0 18px rgba(255, 209, 102, .5);
    }
    .step.done .dot {
      background: var(--green);
      box-shadow: 0 0 18px rgba(78, 240, 163, .48);
    }
    .step strong {
      display: block;
      font-size: 15px;
    }
    .step small {
      display: block;
      color: var(--muted);
      margin-top: 3px;
      font-weight: 650;
    }
    .links {
      display: grid;
      gap: 8px;
      margin-top: 8px;
      color: var(--muted);
      font-size: 13px;
      word-break: break-word;
    }
    .links a {
      color: var(--cyan);
      font-weight: 850;
      text-decoration: none;
    }
    .error {
      display: none;
      color: #ffd1dc;
      border: 1px solid rgba(255, 80, 120, .35);
      background: rgba(255, 80, 120, .14);
      border-radius: 8px;
      padding: 12px;
      font-weight: 750;
    }
    @media (max-width: 860px) {
      body { display: block; }
      .shell { grid-template-columns: 1fr; }
      .hero { min-height: 420px; }
    }
  </style>
</head>
<body>
  <main class="shell">
    <section class="hero">
      <div class="brand"><div class="mark">VS</div><div><span>Vanilla</span><span>Sim</span></div></div>
      <div class="eyebrow" id="phase">Starting OpenSim.exe</div>
      <h1 id="headline">Your world is coming online.</h1>
      <div class="message" id="message">Vanilla Sim is booting. RegionWeb will open automatically as soon as the simulator is ready.</div>
      <div class="progress"><div class="bar"></div></div>
    </section>
    <aside class="side">
      <div class="step done" id="step-config"><div class="dot"></div><div><strong>Configuration written</strong><small>OpenSim.ini, Regions.ini and feature toggles are ready.</small></div></div>
      <div class="step active" id="step-opensim"><div class="dot"></div><div><strong>OpenSim.exe starting</strong><small>Simulator services, regions and modules are loading.</small></div></div>
      <div class="step" id="step-user"><div class="dot"></div><div><strong>First avatar</strong><small>RemoteAdmin will create or confirm the starter account.</small></div></div>
      <div class="step" id="step-regionweb"><div class="dot"></div><div><strong>RegionWeb portal</strong><small>When the portal answers, this page redirects automatically.</small></div></div>
      <div class="error" id="error"></div>
      <div class="links">
        <div>Viewer login: <strong>$loginUri</strong></div>
        <div>Local RegionWeb: <a href="$regionWebUrl">$regionWebUrl</a></div>
        <div>Public RegionWeb: <a href="$publicRegionWebUrl">$publicRegionWebUrl</a></div>
      </div>
    </aside>
  </main>
  <script>
    const phase = document.getElementById('phase');
    const headline = document.getElementById('headline');
    const message = document.getElementById('message');
    const error = document.getElementById('error');
    const stepOpenSim = document.getElementById('step-opensim');
    const stepUser = document.getElementById('step-user');
    const stepRegionWeb = document.getElementById('step-regionweb');
    let redirected = false;

    function mark(el, state) {
      el.classList.remove('active', 'done');
      if (state) el.classList.add(state);
    }

    async function poll() {
      try {
        const res = await fetch('/status', { cache: 'no-store' });
        const data = await res.json();
        phase.textContent = data.phase || 'Starting OpenSim.exe';
        message.textContent = data.message || 'Still starting Vanilla Sim.';

        if (data.error) {
          error.style.display = 'block';
          error.textContent = data.error;
        } else {
          error.style.display = 'none';
        }

        mark(stepOpenSim, data.phase === 'RegionWeb is ready' ? 'done' : 'active');
        mark(stepUser, data.firstUserReady ? 'done' : (data.phase === 'Booting simulator services' ? 'active' : ''));
        mark(stepRegionWeb, data.ready ? 'done' : 'active');

        if (data.ready && !redirected) {
          redirected = true;
          headline.textContent = 'RegionWeb is ready.';
          message.textContent = 'Opening the Vanilla Sim portal now.';
          setTimeout(() => { window.location.href = data.regionWebUrl; }, 1200);
        }
      } catch (e) {
        phase.textContent = 'Waiting for wizard status';
        message.textContent = 'The setup page is still waiting for a status response.';
      }
    }

    poll();
    setInterval(poll, 2500);
  </script>
</body>
</html>
"@
}

function Send-Text($Response, [string]$Body, [string]$ContentType, [int]$StatusCode) {
    $Response.StatusCode = $StatusCode
    $Response.ContentType = $ContentType
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $Response.ContentLength64 = $bytes.Length
    $Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Response.OutputStream.Close()
}

function Send-Json($Response, $Value) {
    $json = $Value | ConvertTo-Json -Compress -Depth 4
    Send-Text $Response $json "application/json; charset=utf-8" 200
}

if (-not (Test-Path $SwitchScript)) {
    throw "Cannot find $SwitchScript."
}

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($Prefix)

try {
    $listener.Start()
}
catch {
    throw "Could not start local setup wizard at $Prefix. Try another port, or run PowerShell as Administrator if Windows reserved the URL."
}

Write-Host "Vanilla Sim setup wizard is running at $Prefix"
Write-Host "Press Ctrl+C here if you want to stop it."

try {
    Start-Process $Prefix | Out-Null
}
catch {
    Write-Host "Open $Prefix in your browser."
}

while ($listener.IsListening) {
    $context = $listener.GetContext()
    $request = $context.Request
    $response = $context.Response

    try {
        if ($request.HttpMethod -eq "GET" -and $request.Url.AbsolutePath -eq "/") {
            Send-Text $response (Render-Page @{} "" "") "text/html; charset=utf-8" 200
            continue
        }

        if ($request.HttpMethod -eq "GET" -and $request.Url.AbsolutePath -eq "/splash") {
            Send-Text $response (Render-SplashPage) "text/html; charset=utf-8" 200
            continue
        }

        if ($request.HttpMethod -eq "GET" -and $request.Url.AbsolutePath -eq "/status") {
            Send-Json $response (Get-SetupStatus)
            continue
        }

        if ($request.HttpMethod -eq "POST" -and $request.Url.AbsolutePath -eq "/apply") {
            $form = Parse-Form $request

            try {
                $settings = Apply-Setup $form
                Start-VanillaSim $settings
                Send-Text $response (Render-SplashPage) "text/html; charset=utf-8" 200
            }
            catch {
                Send-Text $response (Render-Page $form (Html $_.Exception.Message) "error") "text/html; charset=utf-8" 400
            }
            continue
        }

        Send-Text $response "Not found" "text/plain; charset=utf-8" 404
    }
    catch {
        try {
            Send-Text $response (Html $_.Exception.Message) "text/plain; charset=utf-8" 500
        }
        catch {
        }
    }
}
