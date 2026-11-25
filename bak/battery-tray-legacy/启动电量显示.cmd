@echo off
rem 手动启动任务栏电池电量显示（日常由计划任务在登录后自动启动）
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0BatteryTray.ps1"
