@echo off
setlocal EnableExtensions
title RhiGhAI 0.2.0 Installer

set "RHIGHAI_SOURCE=%~dp0Payload\RhiGhAI"
rem One stable, unversioned slot. A versioned folder left every previous build on disk, and Rhino
rem then had several .rhp files claiming the same plug-in GUID.
set "RHIGHAI_ROOT=%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI"
set "RHIGHAI_TARGET=%RHIGHAI_ROOT%"
set "RHIGHAI_RHINO=%ProgramFiles%\Rhino 8\System\Rhino.exe"
set "RHIGHAI_PLUGIN_ID=BC57A265-8A44-4BDB-A887-EA2647812367"
rem Rhino reads the plug-in path from the PlugIn SUBKEY; writing FileName to the parent key is ignored.
set "RHIGHAI_REGISTRY=HKCU\Software\McNeel\Rhinoceros\8.0\Plug-Ins\%RHIGHAI_PLUGIN_ID%"

echo.
echo RhiGhAI 0.2.0 - local Rhino 8 installer
echo.

rem A loaded .rhp cannot be overwritten. Erroring out here just made people re-run the installer in
rem the wrong order, so wait for Rhino to exit instead.
set /a RHIGHAI_WAITED=0
:waitrhino
rem Absolute paths: a PATH carrying Unix tools shadows find.exe and silently breaks this check.
rem CSV output keeps the image name unlocalised, so this works on any Windows UI language.
"%SystemRoot%\System32\tasklist.exe" /FI "IMAGENAME eq Rhino.exe" /FO CSV /NH 2>nul | "%SystemRoot%\System32\find.exe" /I "Rhino.exe" >nul
if errorlevel 1 goto rhinoclosed
if %RHIGHAI_WAITED%==0 (
  echo Rhino is still running. Close every Rhino window now - this installer will continue by itself.
  echo Waiting...
)
if %RHIGHAI_WAITED% GEQ 300 (
  echo.
  echo ERROR: Rhino is still running after 5 minutes. Close it and run this installer again.
  pause
  exit /b 1
)
>nul ping -n 3 127.0.0.1
set /a RHIGHAI_WAITED+=2
goto waitrhino
:rhinoclosed
if %RHIGHAI_WAITED% GTR 0 echo Rhino closed. Continuing.

if not exist "%RHIGHAI_RHINO%" (
  echo ERROR: Rhino 8 was not found at:
  echo   %RHIGHAI_RHINO%
  echo.
  echo Install or update Rhino 8 for Windows, then run this installer again.
  pause
  exit /b 2
)

for %%F in (
  "RhiGhAI.Rhino.rhp"
  "RhiGhAI.Rhino.deps.json"
  "RhiGhAI.Rhino.runtimeconfig.json"
  "RhiGhAI.Core.dll"
  "RhiGhAI.Grasshopper.gha"
  "manifest.yml"
  "NOTICE.txt"
  "README.md"
) do (
  if not exist "%RHIGHAI_SOURCE%\%%~F" (
    echo ERROR: package file is missing: %%~F
    echo Extract the entire installer ZIP before running this file.
    pause
    exit /b 3
  )
)

if not exist "%RHIGHAI_SOURCE%\Runtime\codex.exe" (
  echo ERROR: bundled Codex runtime is missing.
  echo Extract the entire installer ZIP before running this file.
  pause
  exit /b 3
)

rem Remove versioned folders left by 0.1.0 - 0.2.0. Each one held another copy of the same
rem plug-in GUID and another copy of the same Grasshopper .gha.
if exist "%RHIGHAI_ROOT%" (
  for /D %%D in ("%RHIGHAI_ROOT%\*") do (
    if /I not "%%~nxD"=="Runtime" (
      echo Removing stale install: %%~nxD
      rmdir /s /q "%%~fD"
    )
  )
)

rem Check the folder, not errorlevel: a skipped mkdir leaves the previous command's exit code in place.
if not exist "%RHIGHAI_TARGET%" mkdir "%RHIGHAI_TARGET%" 2>nul
if not exist "%RHIGHAI_TARGET%" (
  echo ERROR: could not create the Rhino plug-in folder.
  pause
  exit /b 4
)

for %%F in (
  "RhiGhAI.Rhino.rhp"
  "RhiGhAI.Rhino.deps.json"
  "RhiGhAI.Rhino.runtimeconfig.json"
  "RhiGhAI.Core.dll"
  "RhiGhAI.Grasshopper.gha"
  "manifest.yml"
  "NOTICE.txt"
  "README.md"
) do (
  copy /y "%RHIGHAI_SOURCE%\%%~F" "%RHIGHAI_TARGET%\%%~F" >nul
  if errorlevel 1 (
    echo ERROR: failed to copy %%~F
    pause
    exit /b 5
  )
)

if not exist "%RHIGHAI_TARGET%\Runtime" mkdir "%RHIGHAI_TARGET%\Runtime" 2>nul
if not exist "%RHIGHAI_TARGET%\Runtime" (
  echo ERROR: could not create the Runtime folder.
  pause
  exit /b 4
)
copy /y "%RHIGHAI_SOURCE%\Runtime\codex.exe" "%RHIGHAI_TARGET%\Runtime\codex.exe" >nul
if errorlevel 1 (
  echo ERROR: failed to copy the bundled Codex runtime.
  pause
  exit /b 5
)
if exist "%RHIGHAI_SOURCE%\Runtime\OPENAI-THIRD-PARTY-NOTICES.txt" copy /y "%RHIGHAI_SOURCE%\Runtime\OPENAI-THIRD-PARTY-NOTICES.txt" "%RHIGHAI_TARGET%\Runtime\OPENAI-THIRD-PARTY-NOTICES.txt" >nul

reg.exe add "%RHIGHAI_REGISTRY%" /v Name /t REG_SZ /d "RhiGhAI" /f >nul
if errorlevel 1 (
  echo ERROR: failed to register RhiGhAI for Rhino 8.
  pause
  exit /b 6
)

reg.exe add "%RHIGHAI_REGISTRY%\PlugIn" /v FileName /t REG_SZ /d "%RHIGHAI_TARGET%\RhiGhAI.Rhino.rhp" /f >nul
if errorlevel 1 (
  echo ERROR: failed to register the RhiGhAI plug-in path.
  pause
  exit /b 6
)
rem The parent key kept a stale FileName from earlier installers; drop it so only one path remains.
reg.exe delete "%RHIGHAI_REGISTRY%" /v FileName /f >nul 2>nul

echo.
echo Installed successfully for the current Windows user.
echo.
echo Target:
echo   %RHIGHAI_TARGET%
echo.
echo Registered for Rhino 8 as:
echo   %RHIGHAI_TARGET%\RhiGhAI.Rhino.rhp
echo.
echo Start Rhino 8, then run command: RhiGhAI
echo The panel header must read v0.2.0.
echo.
pause
exit /b 0
