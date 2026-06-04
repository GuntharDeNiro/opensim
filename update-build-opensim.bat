@echo off
setlocal

cd /d "%~dp0"

echo.
echo === Resetting obsolete PowerShell helper ===
git checkout -- Update-Build-Run-OpenSim.ps1 2>NUL

echo.
echo === Updating source from Git ===
set "HAS_LOCAL_CHANGES=0"
git diff --quiet --ignore-submodules --
if errorlevel 1 set "HAS_LOCAL_CHANGES=1"
git diff --cached --quiet --ignore-submodules --
if errorlevel 1 set "HAS_LOCAL_CHANGES=1"

if "%HAS_LOCAL_CHANGES%"=="1" (
    echo Local tracked changes detected; stashing them before pull.
    git stash push -m "update-build-opensim auto-stash before pull"
    if errorlevel 1 goto failed
)

git pull --ff-only
if errorlevel 1 goto failed

echo.
echo === Selecting Windows System.Drawing runtime ===
if exist "bin\System.Drawing.Common.dll.win" (
    copy /Y "bin\System.Drawing.Common.dll.win" "bin\System.Drawing.Common.dll"
    if errorlevel 1 goto failed
)

echo.
echo === Generating project files ===
dotnet bin\prebuild.dll /target vs2022 /targetframework net8_0 /excludedir = "obj | bin" /file prebuild.xml
if errorlevel 1 goto failed

echo.
echo === Building OpenSim Release ===
dotnet build --configuration Release OpenSim.sln
if errorlevel 1 goto failed

echo.
echo === Build complete ===
echo Run OpenSim with:
echo   bin\OpenSim.exe
echo.
pause
exit /b 0

:failed
echo.
echo === FAILED ===
echo Check the error above.
echo.
pause
exit /b 1
