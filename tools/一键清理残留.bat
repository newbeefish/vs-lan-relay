@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo.
echo   正在请求管理员权限...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command','chcp 65001 ^| Out-Null; ^& ''%~dp0cleanup.ps1'''"
exit