@echo off
call "%~dp0build-release.cmd"
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
echo.
pause
