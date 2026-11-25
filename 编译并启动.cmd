@echo off
setlocal
cd /d "%~dp0"
title 编译 Battery Monitor

set "CSC="
if exist "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC goto nocsc

echo.
echo   [1/3] 停止旧实例 ...
taskkill /f /im BatteryMonitor.exe >nul 2>&1
taskkill /f /im BatteryFloat.exe >nul 2>&1

echo   [2/3] 编译 src\*.cs ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:BatteryMonitor.exe /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll src\*.cs > build.log 2>&1
type build.log
if errorlevel 1 goto failed
if exist BatteryFloat.exe del /q BatteryFloat.exe

echo   [3/3] 启动 ...
start "" "%~dp0BatteryMonitor.exe"
echo.
echo   编译完成，悬浮窗已启动。编译日志在 build.log
timeout /t 3 >nul
exit /b 0

:failed
echo.
echo   编译失败。错误明细已写入 build.log，可以直接把这个文件发给助手。
echo.
pause
exit /b 1

:nocsc
echo.
echo   找不到 .NET Framework 自带的 csc.exe，无法编译。
echo.
pause
exit /b 1
