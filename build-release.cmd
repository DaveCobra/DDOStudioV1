@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "APP_VERSION=1.7.2"
set "PORTABLE=%CD%\release\portable\DDO Studio"
set "PREREQS=%CD%\release\prereqs"
set "INSTALLER=%CD%\release\installer"
set "VIEWER_CACHE=%CD%\.build-cache\viewer"
set "PACKAGE_ONLY=0"
if /I "%~1"=="package-only" set "PACKAGE_ONLY=1"

if "%PACKAGE_ONLY%"=="1" goto :package_only_check

echo Stopping any running DDO Studio preview before clearing release output...
call :stop_build_processes
if exist "%CD%\release" rmdir /s /q "%CD%\release" >nul 2>nul
if exist "%CD%\release" (
  echo       Release output is still locked. Stopping stale DDO Studio process names as a fallback...
  taskkill /F /T /IM DDOStudio.exe >nul 2>nul
  taskkill /F /T /IM DdoDatApi.exe >nul 2>nul
  taskkill /F /T /IM DDOGlbExporter.exe >nul 2>nul
  timeout /t 2 /nobreak >nul 2>nul
  rmdir /s /q "%CD%\release" >nul 2>nul
)
if exist "%CD%\release" (
  echo ERROR: Could not clear the previous release output because one or more files are still in use.
  echo        Close DDO Studio and any Explorer window inside release, pause OneDrive sync briefly if needed, then rerun.
  goto :fail
)
mkdir "%PORTABLE%\backend" "%PORTABLE%\exporter" "%PORTABLE%\viewer" "%PREREQS%" "%INSTALLER%" "%VIEWER_CACHE%" >nul 2>nul

echo ============================================================
echo       DDO Studio %APP_VERSION% - Release Builder
echo ============================================================
echo.

where dotnet >nul 2>nul || (
  echo ERROR: .NET 10 SDK is required to build from source.
  exit /b 1
)

echo Preparing NuGet assets for win-x64...
dotnet restore "%CD%\src\DdoDatApi\DdoDatApi.csproj" -r win-x64 --force --no-http-cache || goto :fail
dotnet restore "%CD%\src\DDOExporter\DDOGlbExporter.csproj" -r win-x64 --force --no-http-cache || goto :fail
dotnet restore "%CD%\src\DDOAssetStudio\DDOAssetStudio.csproj" -r win-x64 --force --no-http-cache || goto :fail
echo.

echo [1/7] Publishing embedded DDO DAT backend...
dotnet publish "%CD%\src\DdoDatApi\DdoDatApi.csproj" --no-restore -c Release -r win-x64 --self-contained true -o "%PORTABLE%\backend" || goto :fail

echo [2/7] Publishing general GLB exporter...
dotnet publish "%CD%\src\DDOExporter\DDOGlbExporter.csproj" --no-restore -c Release -r win-x64 --self-contained true -o "%PORTABLE%\exporter" || goto :fail

echo [3/7] Publishing Windows desktop application...
dotnet publish "%CD%\src\DDOAssetStudio\DDOAssetStudio.csproj" --no-restore -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%PORTABLE%" || goto :fail
copy /Y "%CD%\src\DDOAssetStudio\Assets\DDOAssetStudio.ico" "%PORTABLE%\DDOStudio.ico" >nul || goto :fail
copy /Y "%CD%\THIRD_PARTY_NOTICES.txt" "%PORTABLE%\THIRD_PARTY_NOTICES.txt" >nul || goto :fail

echo [4/7] Preparing offline 3D viewer...
xcopy /E /I /Y "%CD%\src\DDOAssetStudio\viewer\*" "%PORTABLE%\viewer\" >nul || (
  echo ERROR: Could not copy the viewer into the portable build.
  goto :fail
)
call :ensure_viewer_lib "three.min.js" "https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js" "https://unpkg.com/three@0.128.0/build/three.min.js" || goto :fail
call :ensure_viewer_lib "GLTFLoader.js" "https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js" "https://unpkg.com/three@0.128.0/examples/js/loaders/GLTFLoader.js" || goto :fail
call :ensure_viewer_lib "RGBELoader.js" "https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/RGBELoader.js" "https://unpkg.com/three@0.128.0/examples/js/loaders/RGBELoader.js" || goto :fail
call :ensure_viewer_lib "OrbitControls.js" "https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/controls/OrbitControls.js" "https://unpkg.com/three@0.128.0/examples/js/controls/OrbitControls.js" || goto :fail

rem Debug symbols can contain local source/build paths. They are not part of the end-user release.
for /r "%PORTABLE%" %%F in (*.pdb) do del /q "%%F" >nul 2>nul

echo [5/7] Preparing the full offline WebView2 runtime installer...
set "WEBVIEW=%PREREQS%\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
if exist "%WEBVIEW%" powershell -NoProfile -ExecutionPolicy Bypass -Command "if((Get-Item -LiteralPath '%WEBVIEW%').Length -lt 50000000){exit 2}" >nul 2>nul
if errorlevel 1 del /q "%WEBVIEW%" >nul 2>nul
if not exist "%WEBVIEW%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';Invoke-WebRequest -UseBasicParsing 'https://go.microsoft.com/fwlink/?LinkId=2124701' -OutFile '%WEBVIEW%';$f=Get-Item -LiteralPath '%WEBVIEW%';if($f.Length -lt 50000000){throw 'Expected the full Evergreen Standalone WebView2 x64 installer, but the download is too small.'};$fs=[IO.File]::OpenRead($f.FullName);try{$a=$fs.ReadByte();$b=$fs.ReadByte()}finally{$fs.Dispose()};if($a -ne 0x4D -or $b -ne 0x5A){throw 'WebView2 download is not a Windows executable.'}" || goto :fail
)

goto :package

:package_only_check
if not exist "%PORTABLE%\DDOStudio.exe" (
  echo ERROR: Portable build is missing. Run build-release.cmd first.
  exit /b 1
)
if not exist "%PREREQS%\MicrosoftEdgeWebView2RuntimeInstallerX64.exe" (
  echo ERROR: The offline WebView2 standalone installer is missing.
  echo Run the full build once so it can be bundled with setup.
  exit /b 1
)
if not exist "%INSTALLER%" mkdir "%INSTALLER%"

:package
echo [6/7] Locating Inno Setup compiler...
set "ISCC="
call :find_inno
if not defined ISCC (
  echo Inno Setup was not found. Installing it on this BUILD PC with Windows Package Manager...
  call :install_inno_with_winget
  call :find_inno
)
if not defined ISCC (
  echo.
  echo ERROR: Inno Setup could not be installed automatically on the build PC.
  echo Install it once with:
  echo   winget install --id JRSoftware.InnoSetup -e -s winget
  echo Then resume with: build-release.cmd package-only
  goto :fail
)

echo [7/7] Building self-contained Windows installer + uninstaller...
"%ISCC%" "%CD%\installer\DDOAssetStudio.iss" || goto :fail

set "BUILT_SETUP=%INSTALLER%\DDOStudio-Setup-%APP_VERSION%.exe"
if not exist "%BUILT_SETUP%" (
  for /f "delims=" %%F in ('dir /b /s "%CD%\release\installer\DDOStudio-Setup-*.exe" 2^>nul') do set "BUILT_SETUP=%%F"
)
if not exist "%BUILT_SETUP%" (
  echo ERROR: Inno Setup completed but no DDO Studio installer could be found.
  goto :fail
)

echo.
echo ============================================================
echo RELEASE COMPLETE
echo.
echo End-user installer:
echo   "%BUILT_SETUP%"
echo.
echo Portable build:
echo   "%PORTABLE%"
echo.
echo No terrain or skybox artwork is bundled. End users can import
echo their own sky/HDR/cubemap imagery; terrain importing is not included.
echo The installer includes .NET app components and the full offline
echo WebView2 x64 runtime prerequisite.
echo ============================================================
exit /b 0

:stop_build_processes
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=[IO.Path]::GetFullPath('%CD%\release');$names=@('DDOStudio.exe','DdoDatApi.exe','DDOGlbExporter.exe');for($pass=0;$pass -lt 3;$pass++){Get-CimInstance Win32_Process -ErrorAction SilentlyContinue ^| Where-Object {$names -contains $_.Name -and (($_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath)).StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0))} ^| ForEach-Object {try{Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop}catch{}};Start-Sleep -Milliseconds 500}" >nul 2>nul
timeout /t 1 /nobreak >nul 2>nul
exit /b 0

:ensure_viewer_lib
set "LIB=%~1"
set "URL1=%~2"
set "URL2=%~3"
if exist "%VIEWER_CACHE%\%LIB%" (
  for %%S in ("%VIEWER_CACHE%\%LIB%") do if %%~zS GEQ 1000 (
    copy /Y "%VIEWER_CACHE%\%LIB%" "%PORTABLE%\viewer\%LIB%" >nul
    echo       OK - %LIB% ^(cache^)
    exit /b 0
  )
)
echo       Downloading %LIB%...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';try{Invoke-WebRequest -UseBasicParsing '%URL1%' -OutFile '%VIEWER_CACHE%\%LIB%'}catch{Invoke-WebRequest -UseBasicParsing '%URL2%' -OutFile '%VIEWER_CACHE%\%LIB%'};if((Get-Item -LiteralPath '%VIEWER_CACHE%\%LIB%').Length -lt 1000){throw '%LIB% download is too small'}" || (
  echo ERROR: Could not obtain %LIB%.
  exit /b 1
)
copy /Y "%VIEWER_CACHE%\%LIB%" "%PORTABLE%\viewer\%LIB%" >nul || exit /b 1
echo       OK - %LIB% ^(downloaded + cached^)
exit /b 0

:find_inno
if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC call :find_inno_x86
exit /b 0

:find_inno_x86
if defined ProgramFiles(x86) if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if not defined ISCC if defined ProgramFiles(x86) if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
exit /b 0

:install_inno_with_winget
where winget >nul 2>nul
if errorlevel 1 exit /b 1
winget install --id JRSoftware.InnoSetup -e -s winget --accept-package-agreements --accept-source-agreements --silent
exit /b %errorlevel%

:fail
echo.
echo RELEASE BUILD FAILED. The first ERROR above identifies the blocking step.
exit /b 1
