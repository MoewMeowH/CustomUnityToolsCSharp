@echo off
rem ============================================================================
rem  HB Chat - chat with the conductor Claude (local web app)
rem    <this>.bat : build (Release) + run in its own window, then open the browser.
rem                 If the port is already listening, only opens the browser.
rem  Port and settings: src\HB.Chat\appsettings.json (+ appsettings.local.json, git-ignored).
rem  Settings are read once at start - restart the app after changing them.
rem ============================================================================
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

set "PORT=5290"
set "URL=http://localhost:%PORT%"
set "PROJ_DIR=%~dp0src\HB.Chat"
set "CONFIGURATION=Release"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found on PATH. Install the .NET SDK first.
    pause
    exit /b 1
)
where claude >nul 2>nul
if errorlevel 1 (
    echo [WARN] claude was not found on PATH. Set Chat:ClaudePath in appsettings.local.json, or sending messages will fail.
)

rem netstat output is localized, so ask PowerShell whether something is listening on the port.
powershell -NoProfile -Command "if (Get-NetTCPConnection -State Listen -LocalPort %PORT% -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }" >nul 2>nul
if not errorlevel 1 (
    echo [Info] Port %PORT% is already in use - HB Chat seems to be running. Opening the browser.
    start "" "%URL%/"
    exit /b 0
)

if not exist "%PROJ_DIR%\HB.Chat.csproj" (
    echo [ERROR] Project not found: %PROJ_DIR%
    pause
    exit /b 1
)

echo [Build] HB Chat...
dotnet build "%PROJ_DIR%\HB.Chat.csproj" -c %CONFIGURATION% --nologo -v q
if errorlevel 1 (
    echo [ERROR] Build failed. See the messages above.
    pause
    exit /b 1
)

rem Runs in its own window. Closing that window stops the app (stop a running reply with the Stop button first).
start "HB Chat" /D "%PROJ_DIR%" cmd /k dotnet run --no-build -c %CONFIGURATION% -- --urls=%URL%

echo [Info] HB Chat started in a separate window. Close that window to stop it.
timeout /t 4 /nobreak >nul
start "" "%URL%/"
exit /b 0
