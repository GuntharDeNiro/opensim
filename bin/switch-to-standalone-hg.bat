@echo off
setlocal

set "HG_HOST=%~1"
if "%HG_HOST%"=="" set "HG_HOST=vanilla-sim.com"

pushd "%~dp0" >nul

echo.
echo === Switching OpenSim to Standalone Hypergrid ===
echo Hostname: %HG_HOST%
echo Regions.ini will not be touched.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0config-profiles\switch-to-standalone-hg.ps1" -HostName "%HG_HOST%"
set "SWITCH_RESULT=%ERRORLEVEL%"

if not "%SWITCH_RESULT%"=="0" (
    echo.
    echo === FAILED: Standalone Hypergrid switch did not complete ===
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
