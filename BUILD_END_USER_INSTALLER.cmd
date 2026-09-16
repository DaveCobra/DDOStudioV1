@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title DDO Studio 1.7.2 - Build End User Installer

echo ============================================================
echo   DDO Studio 1.7.2 - Build End User Installer
echo ============================================================
echo.

echo [1/2] Removing obsolete bundled-environment files...
call apply-update-cleanup.cmd
if errorlevel 1 goto :fail

echo.
echo [2/2] Building the Windows installer...
call build-release.cmd
if errorlevel 1 goto :fail

set "SETUP=%CD%\release\installer\DDOStudio-Setup-1.7.2.exe"
if not exist "%SETUP%" (
  echo ERROR: Build completed but the expected installer was not found:
  echo        "%SETUP%"
  goto :fail
)

echo.
echo ============================================================
echo SUCCESS
echo End-user installer:
echo   "%SETUP%"
echo ============================================================
echo.
explorer.exe /select,"%SETUP%"
pause
exit /b 0

:fail
echo.
echo ============================================================
echo INSTALLER BUILD FAILED.
echo The first ERROR above identifies the blocking step.
echo ============================================================
echo.
pause
exit /b 1
