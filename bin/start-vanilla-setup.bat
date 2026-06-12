@echo off
setlocal

pushd "%~dp0" >nul

echo.
echo === Vanilla Sim First-Run Setup ===
echo This opens a local setup wizard at http://127.0.0.1:9090
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0config-profiles\vanilla-first-run-setup.ps1"
set "SETUP_RESULT=%ERRORLEVEL%"

if not "%SETUP_RESULT%"=="0" (
    echo.
    echo === FAILED: Vanilla Sim setup wizard stopped with an error ===
    popd >nul
    pause
    exit /b %SETUP_RESULT%
)

popd >nul
exit /b 0
