@echo off
setlocal

pushd "%~dp0" >nul

echo.
echo === Switching OpenSim to captured OSGrid profile ===
echo Regions.ini will not be touched.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0config-profiles\switch-to-osgrid.ps1"
set "SWITCH_RESULT=%ERRORLEVEL%"

if not "%SWITCH_RESULT%"=="0" (
    echo.
    echo === FAILED: OSGrid switch did not complete ===
    echo If this is the first time, capture your working OSGrid profile with:
    echo   powershell -NoProfile -ExecutionPolicy Bypass -File .\config-profiles\capture-osgrid-profile.ps1
    echo.
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
