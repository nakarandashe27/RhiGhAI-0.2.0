@echo off
setlocal EnableExtensions
title RhiGhAI 0.2.4 Installer
echo RhiGhAI 0.2.4 - close Rhino before installing.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-RhiGhAI.ps1"
if errorlevel 1 (
  echo Installation failed. See the message above; the previous installation is preserved.
  pause
  exit /b 1
)
pause
exit /b 0
