@echo off
setlocal
cd /d "%~dp0"
taskkill /f /im BatteryMonitor.exe >nul 2>&1
taskkill /f /im BatteryFloat.exe >nul 2>&1
echo   已停止 Battery Monitor
rem 旧版 ps1 实现已归档到 bak\legacy，这里只收 exe 实例
echo.
timeout /t 3 >nul
