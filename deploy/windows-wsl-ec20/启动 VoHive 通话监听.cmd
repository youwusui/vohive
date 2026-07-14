@echo off
setlocal
chcp 65001 >nul
title VoHive Call Listener
pushd "%~dp0"
if not exist "%~dp0config\vohive-wsl.json" (
  echo Missing config\vohive-wsl.json
  echo Copy config\vohive-wsl.example.json to config\vohive-wsl.json and edit it first.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start VoHive Call Listener.ps1" -PlayLive
set "RC=%ERRORLEVEL%"
echo.
echo Exit code: %RC%
pause
popd
exit /b %RC%
