@echo off
setlocal

set "HG_HOST=%~1"
if "%HG_HOST%"=="" set "HG_HOST=vanilla-sim.com"

pushd "%~dp0" >nul

echo.
echo === Switching OpenSim to Standalone Hypergrid + MultiGrid attachments ===
echo Hostname: %HG_HOST%
echo Primary grid: local Standalone Hypergrid
echo Secondary attachments: OSGrid, Neverworld Grid, ZetaWorlds, Craft World
echo Regions.ini will not be touched.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0config-profiles\switch-to-standalone-hg.ps1" -HostName "%HG_HOST%" -AttachPublicGrids
set "SWITCH_RESULT=%ERRORLEVEL%"

if not "%SWITCH_RESULT%"=="0" (
    echo.
    echo === FAILED: MultiGrid switch did not complete ===
    popd >nul
    pause
    exit /b %SWITCH_RESULT%
)

echo.
echo === DONE ===
echo Start the simulator with:
echo   OpenSim.exe
echo.

popd >nul
pause
exit /b 0
