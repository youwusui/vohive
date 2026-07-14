@echo off
setlocal
chcp 65001 >nul
if exist "%~dp0config\vohive-wsl.json" (
  echo config\vohive-wsl.json already exists.
) else (
  copy /Y "%~dp0config\vohive-wsl.example.json" "%~dp0config\vohive-wsl.json" >nul
  echo Created config\vohive-wsl.json. Edit it before first start.
)
pause
