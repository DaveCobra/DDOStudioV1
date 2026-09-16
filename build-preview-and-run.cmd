@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "APP_VERSION=1.7.2"
set "OUT=%CD%\release\preview\DDO Studio"
set "VIEWER_CACHE=%CD%\.build-cache\viewer"

echo ============================================================
echo        DDO Studio %APP_VERSION% - GUI Preview Builder
echo ============================================================
where dotnet >nul 2>nul || (echo ERROR: .NET 10 SDK is required. & exit /b 1)

echo Stopping any previous preview instance from this source tree...
call :stop_preview_processes
if exist "%OUT%" rmdir /s /q "%OUT%" >nul 2>nul
if exist "%OUT%" (
  echo       Preview output is still locked. Stopping stale DDO Studio process names as a fallback...
  taskkill /F /T /IM DDOStudio.exe >nul 2>nul
  taskkill /F /T /IM DdoDatApi.exe >nul 2>nul
  taskkill /F /T /IM DDOGlbExporter.exe >nul 2>nul
  timeout /t 2 /nobreak >nul 2>nul
  rmdir /s /q "%OUT%" >nul 2>nul
)
if exist "%OUT%" (
  echo ERROR: Could not clear the previous preview output. A DDO Studio/backend process or another program still has files open.
  echo        Close any Explorer window showing release\preview, pause OneDrive sync briefly if needed, then rerun this script.
  goto :fail
)
mkdir "%OUT%\backend" "%OUT%\exporter" "%OUT%\viewer" "%VIEWER_CACHE%" >nul 2>nul

echo [1/5] Restoring Windows build dependencies...
dotnet restore "%CD%\src\DdoDatApi\DdoDatApi.csproj" -r win-x64 --force --no-http-cache || goto :fail
dotnet restore "%CD%\src\DDOExporter\DDOGlbExporter.csproj" -r win-x64 --force --no-http-cache || goto :fail
dotnet restore "%CD%\src\DDOAssetStudio\DDOAssetStudio.csproj" -r win-x64 --force --no-http-cache || goto :fail

echo [2/5] Building DDO DAT backend...
dotnet publish "%CD%\src\DdoDatApi\DdoDatApi.csproj" --no-restore -c Release -r win-x64 --self-contained true -o "%OUT%\backend" || goto :fail

echo [3/5] Building GLB exporter...
dotnet publish "%CD%\src\DDOExporter\DDOGlbExporter.csproj" --no-restore -c Release -r win-x64 --self-contained true -o "%OUT%\exporter" || goto :fail

echo [4/5] Building DDO Studio Windows shell...
dotnet publish "%CD%\src\DDOAssetStudio\DDOAssetStudio.csproj" --no-restore -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%OUT%" || goto :fail
copy /Y "%CD%\src\DDOAssetStudio\Assets\DDOAssetStudio.ico" "%OUT%\DDOStudio.ico" >nul || goto :fail

echo [5/5] Preparing Scene Studio viewer...
xcopy /E /I /Y "%CD%\src\DDOAssetStudio\viewer\*" "%OUT%\viewer\" >nul || goto :fail
call :ensure_viewer_lib "three.min.js" "https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js" "https://unpkg.com/three@0.128.0/build/three.min.js" || goto :fail
call :ensure_viewer_lib "GLTFLoader.js" "https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js" "https://unpkg.com/three@0.128.0/examples/js/loaders/GLTFLoader.js" || goto :fail
call :ensure_viewer_lib "RGBELoader.js" "https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/RGBELoader.js" "https://unpkg.com/three@0.128.0/examples/js/loaders/RGBELoader.js" || goto :fail
call :ensure_viewer_lib "OrbitControls.js" "https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/controls/OrbitControls.js" "https://unpkg.com/three@0.128.0/examples/js/controls/OrbitControls.js" || goto :fail
if not exist "%OUT%\DDOStudio.exe" goto :fail
start "" "%OUT%\DDOStudio.exe"
exit /b 0


:stop_preview_processes
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=[IO.Path]::GetFullPath('%OUT%');$names=@('DDOStudio.exe','DdoDatApi.exe','DDOGlbExporter.exe');for($pass=0;$pass -lt 3;$pass++){Get-CimInstance Win32_Process -ErrorAction SilentlyContinue ^| Where-Object {$names -contains $_.Name -and (($_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath)).StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))} ^| ForEach-Object {try{Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop}catch{}};Start-Sleep -Milliseconds 500}" >nul 2>nul
timeout /t 1 /nobreak >nul 2>nul
exit /b 0

:ensure_viewer_lib
set "LIB=%~1"
set "URL1=%~2"
set "URL2=%~3"
if exist "%VIEWER_CACHE%\%LIB%" (
  for %%S in ("%VIEWER_CACHE%\%LIB%") do if %%~zS GEQ 1000 (
    copy /Y "%VIEWER_CACHE%\%LIB%" "%OUT%\viewer\%LIB%" >nul
    echo       OK - %LIB% ^(cache^)
    exit /b 0
  )
)
echo       Downloading %LIB%...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';try{Invoke-WebRequest -UseBasicParsing '%URL1%' -OutFile '%VIEWER_CACHE%\%LIB%'}catch{Invoke-WebRequest -UseBasicParsing '%URL2%' -OutFile '%VIEWER_CACHE%\%LIB%'};if((Get-Item -LiteralPath '%VIEWER_CACHE%\%LIB%').Length -lt 1000){throw '%LIB% download is too small'}" || exit /b 1
copy /Y "%VIEWER_CACHE%\%LIB%" "%OUT%\viewer\%LIB%" >nul || exit /b 1
echo       OK - %LIB% ^(downloaded + cached^)
exit /b 0

:fail
echo.
echo PREVIEW BUILD FAILED. The first ERROR above identifies the blocking step.
exit /b 1
