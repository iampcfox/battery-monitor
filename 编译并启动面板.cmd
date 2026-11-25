@echo off
setlocal
cd /d "%~dp0ui"
title Battery Monitor Panel

set "DOTNET=C:\Program Files\dotnet\x64\dotnet.exe"
if exist "%DOTNET%" goto have
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if exist "%DOTNET%" goto have
echo.
echo   [X] dotnet.exe not found. Please install .NET 8 SDK first.
echo.
pause
exit /b 1

:have
echo.
echo   [1/2] Building WinUI 3 panel ...
"%DOTNET%" build BatteryMonitor.UI.csproj -c Debug
if errorlevel 1 goto failed

echo.
echo   [2/2] Launching panel ...
start "" "%~dp0ui\bin\Debug\net8.0-windows10.0.19041.0\win-x64\BatteryMonitor.UI.exe"
echo.
echo   Done. This console closes in 5 seconds.
timeout /t 5 >nul
exit /b 0

:failed
echo.
echo   Build failed. Send the errors above to the assistant.
echo.
pause
exit /b 1
