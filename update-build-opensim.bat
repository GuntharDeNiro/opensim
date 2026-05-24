@echo off
setlocal

cd /d "%~dp0"

echo.
echo === Updating source from Git ===
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
