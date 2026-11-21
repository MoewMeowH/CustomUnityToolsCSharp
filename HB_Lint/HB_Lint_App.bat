@echo off
rem ============================================================================
rem  HB_Lint rule editor - window app (hblint-app-desktop, WebView2).
rem    <this>.bat : build the web UI (npm) and the window app (Release), then start it.
rem                 In the window, "Open Unity project" picks a Unity project folder and sets up Assets/HBLint.
rem  Arguments are passed to the app (--rules / --out / --project / --port), e.g.  HB_Lint_App.bat --project D:\Game
rem  If the app is already running, this starts another window without rebuilding (the exe is in use).
rem  After the first build you can also pin src\HBLint.App.Desktop\bin\Release\net10.0-windows\hblint-app-desktop.exe.
rem  Needs: .NET SDK 10, Node.js (npm), WebView2 runtime.
rem ============================================================================
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

set "CONFIGURATION=Release"
set "WEB=%~dp0src\HBLint.App\web"
set "DESK_PROJ=%~dp0src\HBLint.App.Desktop\HBLint.App.Desktop.csproj"
set "DESK_EXE=%~dp0src\HBLint.App.Desktop\bin\%CONFIGURATION%\net10.0-windows\hblint-app-desktop.exe"

tasklist /FI "IMAGENAME eq hblint-app-desktop.exe" 2>nul | find /I "hblint-app-desktop.exe" >nul
if not errorlevel 1 (
    if not exist "%DESK_EXE%" (
        echo [ERROR] hblint-app-desktop is running but the Release build is missing - close it and run this again to build.
        pause
        exit /b 1
    )
    echo [Info] hblint-app-desktop is already running - opening another window without rebuilding.
    start "" "%DESK_EXE%" %*
    exit /b 0
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found on PATH. Install the .NET SDK 10 first.
    pause
    exit /b 1
)
where npm >nul 2>nul
if errorlevel 1 (
    echo [ERROR] npm was not found on PATH. Install Node.js first.
    pause
    exit /b 1
)

if not exist "%WEB%\node_modules" (
    echo [Build] npm ci...
    call npm ci --prefix "%WEB%" --no-audit --no-fund
    if errorlevel 1 goto :fail
)
echo [Build] web UI...
call npm run build --prefix "%WEB%"
if errorlevel 1 goto :fail

echo [Build] window app...
dotnet build "%DESK_PROJ%" -c %CONFIGURATION% --nologo -v q
if errorlevel 1 goto :fail

echo [Info] Starting hblint-app-desktop.
start "" "%DESK_EXE%" %*
exit /b 0

:fail
echo [ERROR] Build failed. See the messages above.
pause
exit /b 1
