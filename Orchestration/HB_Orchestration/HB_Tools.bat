@echo off
rem ============================================================================
rem  HB Orchestration tools
rem    HB_Tools.bat             : menu
rem    HB_Tools.bat publish     : publish MCP server only
rem    HB_Tools.bat dashboard   : build + run dashboard only
rem    HB_Tools.bat all         : publish, then dashboard
rem  Paths and options are read from HB_Tools.config (edit with Notepad).
rem ============================================================================
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
set "CONFIG_FILE=%~dp0HB_Tools.config"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found on PATH. Install the .NET SDK first.
    pause
    exit /b 1
)

call :load_config

if /i "%~1"=="publish"   goto :arg_publish
if /i "%~1"=="dashboard" goto :arg_dashboard
if /i "%~1"=="all"       goto :arg_all
if not "%~1"=="" (
    echo [ERROR] Unknown argument: %~1   - use publish, dashboard or all
    exit /b 2
)

:menu
echo.
echo ===========================================================
echo   HB Orchestration Tools
echo ===========================================================
echo   Solution  : %SOLUTION_DIR%
echo   Publish   : %PUBLISH_DIR%
echo   Build     : %CONFIGURATION%
echo   Dashboard : %DASH_URL%
echo -----------------------------------------------------------
echo   1. Publish MCP server
echo   2. Run dashboard
echo   3. Publish MCP server + Run dashboard
echo   4. Edit settings  - HB_Tools.config
echo   0. Exit
echo ===========================================================
set "CHOICE="
set /p "CHOICE=Select: "
if "%CHOICE%"=="1" goto :do_publish
if "%CHOICE%"=="2" goto :do_dashboard
if "%CHOICE%"=="3" goto :do_all
if "%CHOICE%"=="4" goto :do_config
if "%CHOICE%"=="0" goto :eof
echo [WARN] Invalid choice.
goto :menu

:do_publish
call :publish
goto :menu

:do_dashboard
call :dashboard
goto :menu

:do_all
call :publish
call :dashboard
goto :menu

:do_config
if not exist "%CONFIG_FILE%" (
    echo [ERROR] HB_Tools.config not found next to this batch file.
    goto :menu
)
echo [Info] Opening HB_Tools.config. Save and close Notepad to apply.
start "" /wait notepad "%CONFIG_FILE%"
call :load_config
echo [Info] Settings reloaded.
goto :menu

:arg_publish
call :publish
exit /b %errorlevel%

:arg_dashboard
call :dashboard
exit /b %errorlevel%

:arg_all
call :publish
call :dashboard
exit /b %errorlevel%


rem ============================================================================
rem  Settings
rem ============================================================================
:load_config
rem defaults - any key missing or empty in HB_Tools.config falls back to these
set "SOLUTION_DIR=."
set "PUBLISH_DIR=..\Publish_Output"
set "CONFIGURATION=Release"
set "DASHBOARD_PORT=5280"

if exist "%CONFIG_FILE%" (
    for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%CONFIG_FILE%") do (
        if not "%%B"=="" set "%%A=%%B"
    )
) else (
    echo [WARN] HB_Tools.config not found. Using defaults.
)

call :resolve SOLUTION_DIR
call :resolve PUBLISH_DIR

set "MCP_SRC=%SOLUTION_DIR%\src\HB.Orchestration.Mcp"
set "MCP_PROJ=%MCP_SRC%\HB.Orchestration.Mcp.csproj"
set "MCP_DLL=%PUBLISH_DIR%\HB.Orchestration.Mcp.dll"
set "DASH_DIR=%SOLUTION_DIR%\src\HB.Orchestration.Dashboard"
set "DASH_PROJ=%DASH_DIR%\HB.Orchestration.Dashboard.csproj"
set "DASH_URL=http://localhost:%DASHBOARD_PORT%"
exit /b 0

:resolve
rem %1 = variable name. Makes the path absolute - relative to this batch folder - and strips a trailing backslash.
call set "_RESOLVE=%%%~1%%"
for %%I in ("%_RESOLVE%") do set "_RESOLVE=%%~fI"
if "%_RESOLVE:~-1%"=="\" set "_RESOLVE=%_RESOLVE:~0,-1%"
set "%~1=%_RESOLVE%"
set "_RESOLVE="
exit /b 0


rem ============================================================================
rem  Publish MCP server
rem ============================================================================
:publish
echo.
echo [Publish] %MCP_PROJ%
echo           to %PUBLISH_DIR%

if not exist "%MCP_PROJ%" (
    echo [ERROR] MCP project not found. Check SOLUTION_DIR in HB_Tools.config.
    exit /b 1
)

rem Claude Code keeps the MCP server running, which locks the dll and makes publish fail half-way.
set "LOCKED="
if exist "%MCP_DLL%" (
    2>nul ( >>"%MCP_DLL%" (call ) ) || set "LOCKED=1"
)
if defined LOCKED (
    echo [ERROR] HB.Orchestration.Mcp.dll is in use.
    echo         Close every Claude Code session that uses the hb-orchestration server, then try again.
    exit /b 1
)

if not exist "%PUBLISH_DIR%" mkdir "%PUBLISH_DIR%"

dotnet publish "%MCP_PROJ%" -c %CONFIGURATION% -o "%PUBLISH_DIR%" --nologo
if errorlevel 1 (
    echo [ERROR] Publish failed. See the messages above.
    exit /b 1
)

rem Config files are never overwritten by publish - copy defaults only when missing.
for %%F in (appsettings.json modes.json) do (
    if not exist "%PUBLISH_DIR%\%%F" (
        copy /y "%MCP_SRC%\%%F" "%PUBLISH_DIR%\%%F" >nul
        echo [Info] %%F was missing - copied the default from source.
    ) else (
        echo [Info] Kept your existing %%F
    )
)

echo [OK] Publish complete. Restart Claude Code to load the new server.
exit /b 0


rem ============================================================================
rem  Dashboard
rem ============================================================================
:dashboard
echo.
echo [Dashboard] %DASH_URL%

rem netstat output is localized, so ask PowerShell whether something is listening on the port.
powershell -NoProfile -Command "if (Get-NetTCPConnection -State Listen -LocalPort %DASHBOARD_PORT% -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }" >nul 2>nul
if not errorlevel 1 (
    echo [Info] Port %DASHBOARD_PORT% is already in use - the dashboard seems to be running. Opening the browser.
    start "" "%DASH_URL%/"
    exit /b 0
)

if not exist "%DASH_PROJ%" (
    echo [ERROR] Dashboard project not found. Check SOLUTION_DIR in HB_Tools.config.
    exit /b 1
)

echo [Build] Dashboard...
dotnet build "%DASH_PROJ%" -c %CONFIGURATION% --nologo -v q
if errorlevel 1 (
    echo [ERROR] Dashboard build failed. See the messages above.
    exit /b 1
)

rem Runs in its own window. The publish folder is passed so the Modes page edits the right files.
start "HB Dashboard" /D "%DASH_DIR%" cmd /k dotnet run --no-build -c %CONFIGURATION% -- --urls=%DASH_URL% "--Dashboard:McpConfigDir=%PUBLISH_DIR%"

echo [Info] Dashboard started in a separate window. Close that window to stop it.
timeout /t 4 /nobreak >nul
start "" "%DASH_URL%/"
exit /b 0
