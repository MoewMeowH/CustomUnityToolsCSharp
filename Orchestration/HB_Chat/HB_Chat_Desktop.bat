@echo off
rem ============================================================================
rem  HB Chat Desktop - the chat app and the orchestration dashboard in one window (WebView2 tabs)
rem    <this>.bat : build (Release) the chat server, the dashboard and the desktop app, then start the desktop app.
rem                 The desktop app starts both servers hidden (or attaches to ones already running) and stops
rem                 the ones it started when it exits.
rem  A server whose port is already in use is not rebuilt (its exe is locked by the running server).
rem  If the desktop app is already running, this only brings its window to the front.
rem  Desktop settings: %LOCALAPPDATA%\HB_Chat\desktop.json (optional). Server logs: %LOCALAPPDATA%\HB_Chat\logs.
rem ============================================================================
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

set "CONFIGURATION=Release"
set "CHAT_PORT=5290"
set "DASH_PORT=5280"
set "CHAT_PROJ=%~dp0src\HB.Chat\HB.Chat.csproj"
set "DASH_PROJ=%~dp0..\HB_Orchestration\src\HB.Orchestration.Dashboard\HB.Orchestration.Dashboard.csproj"
set "DESK_PROJ=%~dp0src\HB.Chat.Desktop\HB.Chat.Desktop.csproj"
set "DESK_EXE=%~dp0src\HB.Chat.Desktop\bin\%CONFIGURATION%\net10.0-windows10.0.17763.0\HB.Chat.Desktop.exe"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found on PATH. Install the .NET SDK first.
    pause
    exit /b 1
)

rem Already running: starting it again just brings the existing window to the front.
tasklist /FI "IMAGENAME eq HB.Chat.Desktop.exe" 2>nul | find /I "HB.Chat.Desktop.exe" >nul
if not errorlevel 1 (
    echo [Info] HB Chat Desktop is already running - bringing it to the front.
    if exist "%DESK_EXE%" start "" "%DESK_EXE%"
    exit /b 0
)

call :build_if_free "%CHAT_PORT%" "%CHAT_PROJ%" "chat server"
if errorlevel 1 goto :fail
call :build_if_free "%DASH_PORT%" "%DASH_PROJ%" "dashboard"
if errorlevel 1 goto :fail

echo [Build] desktop app...
dotnet build "%DESK_PROJ%" -c %CONFIGURATION% --nologo -v q
if errorlevel 1 goto :fail

echo [Info] Starting HB Chat Desktop.
start "" "%DESK_EXE%"
exit /b 0

:fail
echo [ERROR] Build failed. See the messages above.
pause
exit /b 1

rem %1 = port, %2 = project, %3 = name. Skips the build when something already listens on the port.
:build_if_free
powershell -NoProfile -Command "if (Get-NetTCPConnection -State Listen -LocalPort %~1 -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }" >nul 2>nul
if not errorlevel 1 (
    echo [Info] Port %~1 is in use - the %~3 seems to be running. Not rebuilding it; the desktop app will attach to it.
    exit /b 0
)
if not exist "%~2" (
    echo [ERROR] Project not found: %~2
    exit /b 1
)
echo [Build] %~3...
dotnet build "%~2" -c %CONFIGURATION% --nologo -v q
exit /b %errorlevel%
