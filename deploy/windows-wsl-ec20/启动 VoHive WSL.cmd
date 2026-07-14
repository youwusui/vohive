@echo off
setlocal
chcp 65001 >nul
title Start VoHive WSL
pushd "%~dp0"
if not exist "%~dp0config\vohive-wsl.json" (
  echo Missing config\vohive-wsl.json
  echo Copy config\vohive-wsl.example.json to config\vohive-wsl.json and edit it first.
  pause
  exit /b 1
)
net session >nul 2>nul
if not "%ERRORLEVEL%"=="0" (
  set "VOHIVE_SELF=%~f0"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:VOHIVE_SELF -WorkingDirectory '%~dp0' -Verb RunAs"
  exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start VoHive WSL.ps1"
set "RC=%ERRORLEVEL%"
echo.
echo Exit code: %RC%
pause
popd
exit /b %RC%
