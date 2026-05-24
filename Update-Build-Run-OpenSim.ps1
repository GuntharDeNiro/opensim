param(
    [string]$RepoPath = $PSScriptRoot,
    [string]$Remote = "origin",
    [string]$Branch = "",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string[]]$OpenSimArgs = @(),
    [switch]$NoPull,
    [switch]$NoPrebuild,
    [switch]$NoRun,
    [switch]$Detached
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    Write-Host "+ $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath"
    }
}

if (-not (Test-Path $RepoPath)) {
    throw "RepoPath does not exist: $RepoPath"
}

$RepoPath = (Resolve-Path $RepoPath).Path
$BinPath = Join-Path $RepoPath "bin"
$OpenSimExe = Join-Path $BinPath "OpenSim.exe"
$WindowsDrawingDll = Join-Path $BinPath "System.Drawing.Common.dll.win"
$DrawingDll = Join-Path $BinPath "System.Drawing.Common.dll"

Push-Location $RepoPath
try {
    if (-not $NoPull) {
        Write-Step "Updating source from GitHub"
        Invoke-Checked "git" @("fetch", $Remote, "--prune")

        if ($Branch -ne "") {
            Invoke-Checked "git" @("checkout", $Branch)
            Invoke-Checked "git" @("pull", "--ff-only", $Remote, $Branch)
        }
        else {
            Invoke-Checked "git" @("pull", "--ff-only")
        }
    }

    if (Test-Path $WindowsDrawingDll) {
        Write-Step "Selecting Windows System.Drawing runtime"
        Copy-Item -Path $WindowsDrawingDll -Destination $DrawingDll -Force
    }

    if (-not $NoPrebuild) {
        Write-Step "Generating Visual Studio project files"
        Invoke-Checked "dotnet" @(
            "bin\prebuild.dll",
            "/target", "vs2022",
            "/targetframework", "net8_0",
            "/excludedir", "=", "obj | bin",
            "/file", "prebuild.xml"
        )
    }

    Write-Step "Building OpenSim ($Configuration)"
    Invoke-Checked "dotnet" @("build", "--configuration", $Configuration, "OpenSim.sln")

    if (-not (Test-Path $OpenSimExe)) {
        throw "Build completed but OpenSim.exe was not found: $OpenSimExe"
    }

    if (-not $NoRun) {
        Write-Step "Starting OpenSim"
        if ($Detached) {
            Start-Process -FilePath $OpenSimExe -ArgumentList $OpenSimArgs -WorkingDirectory $BinPath
            Write-Host "OpenSim started in detached mode."
        }
        else {
            Push-Location $BinPath
            try {
                & $OpenSimExe @OpenSimArgs
            }
            finally {
                Pop-Location
            }
        }
    }
}
finally {
    Pop-Location
}
