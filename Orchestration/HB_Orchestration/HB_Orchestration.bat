@echo off
rem ============================================================================
rem  HB Orchestration tools
rem    <this>.bat             : menu
rem    <this>.bat publish     : publish MCP server only
rem    <this>.bat dashboard   : build + run dashboard only
rem    <this>.bat all         : publish, then dashboard
rem    add --yes after publish or all to stop a running MCP server without asking
rem  Settings are read from the .config file with the SAME NAME as this batch file
rem  (HB_Orchestration.bat -> HB_Orchestration.config). Rename both together.
rem ============================================================================
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
set "CONFIG_FILE=%~dpn0.config"
set "CONFIG_NAME=%~n0.config"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found on PATH. Install the .NET SDK first.
    pause
    exit /b 1
)

call :load_config

set "ASSUME_YES="
if /i "%~2"=="--yes" set "ASSUME_YES=1"
if not "%~2"=="" if not defined ASSUME_YES (
    echo [ERROR] Unknown option: %~2   - only --yes is accepted after publish or all
    exit /b 2
)

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
echo   4. Edit settings  - %CONFIG_NAME%
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
    echo [ERROR] %CONFIG_NAME% not found next to this batch file.
    goto :menu
)
echo [Info] Opening %CONFIG_NAME%. Save and close Notepad to apply.
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
rem defaults - any key missing or empty in the config falls back to these
set "SOLUTION_DIR=."
set "PUBLISH_DIR=..\Publish_Output"
set "CONFIGURATION=Release"
set "DASHBOARD_PORT=5280"

if exist "%CONFIG_FILE%" (
    for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%CONFIG_FILE%") do (
        if not "%%B"=="" set "%%A=%%B"
    )
) else (
    echo [WARN] %CONFIG_NAME% not found. Using defaults.
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
:confirm_stop
rem Returns 0 only for an explicit yes. "choice" did not cancel when its input was redirected
rem (2026-10-22: it printed "Input redirection is not supported" and publish went on), so this uses
rem set /p with No as the default: empty input, end of input or anything but Y cancels.
rem "publish --yes" skips the question (for runs without a console).
if /i "%ASSUME_YES%"=="1" (
    echo [Info] --yes given: stopping the MCP server without asking.
    exit /b 0
)
set "ANSWER="
set /p "ANSWER=Stop the MCP server now and publish? [y/N] "
if /i "%ANSWER%"=="Y" exit /b 0
if /i "%ANSWER%"=="YES" exit /b 0
exit /b 1

:publish
echo.
echo [Publish] %MCP_PROJ%
echo           to %PUBLISH_DIR%

if not exist "%MCP_PROJ%" (
    echo [ERROR] MCP project not found. Check SOLUTION_DIR in %CONFIG_NAME%.
    exit /b 1
)

rem Claude Code keeps the MCP server running, which locks the dll and makes publish fail half-way.
rem Only the MCP server processes are stopped - Claude Code and its conversation keep running.
set "STOPPED="
powershell -NoProfile -Command "exit @(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*HB.Orchestration.Mcp.dll*' }).Count" >nul 2>nul
set "MCP_COUNT=%errorlevel%"
if not "%MCP_COUNT%"=="0" (
    echo [Info] Running MCP server processes: %MCP_COUNT%
    echo        They must be stopped to publish. Claude Code itself keeps running.
    call :confirm_stop
    if errorlevel 1 (
        echo [Info] Publish cancelled.
        exit /b 1
    )
    powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*HB.Orchestration.Mcp.dll*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }" >nul 2>nul
    timeout /t 2 /nobreak >nul
    set "STOPPED=1"
)

set "LOCKED="
if exist "%MCP_DLL%" (
    2>nul ( >>"%MCP_DLL%" (call ) ) || set "LOCKED=1"
)
if defined LOCKED (
    echo [ERROR] HB.Orchestration.Mcp.dll is still in use.
    echo         Close the Claude Code sessions that use the hb-orchestration server, then try again.
    exit /b 1
)

if not exist "%PUBLISH_DIR%" mkdir "%PUBLISH_DIR%"

rem Back up the live config first. The SDK can delete files it published last time but not this time
rem (incremental publish clean), so the config files are restored from this backup after publish.
rem Each run gets its own folder and earlier backups are kept (the newest CFG_BACKUP_KEEP runs),
rem so a wrong restore can still be undone by hand. If the backup fails, nothing is published.
set "CFG_BACKUP_ROOT=%LOCALAPPDATA%\HB_Orchestration\config-backup"
set "CFG_BACKUP_KEEP=20"
set "CFG_STAMP="
for /f "usebackq delims=" %%T in (`powershell -NoProfile -Command "(Get-Date).ToString('yyyyMMdd-HHmmss') + '-' + (Get-Random -Maximum 10000).ToString('0000')"`) do set "CFG_STAMP=%%T"
set "CFG_BACKUP=%CFG_BACKUP_ROOT%\%CFG_STAMP%"
if not defined CFG_STAMP goto :backup_failed
rem A new folder only: reusing one that already exists could restore files left from an earlier run.
if exist "%CFG_BACKUP%" goto :backup_failed
mkdir "%CFG_BACKUP%" >nul 2>nul || goto :backup_failed
if not exist "%CFG_BACKUP%\" goto :backup_failed
set "BACKUP_FAILED="
for %%F in (appsettings.json modes.json) do (
    if exist "%PUBLISH_DIR%\%%F" (
        copy /y "%PUBLISH_DIR%\%%F" "%CFG_BACKUP%\%%F" >nul || set "BACKUP_FAILED=1"
    )
)
if defined BACKUP_FAILED goto :backup_failed

rem Prune old backups: only folders named like this script's stamps, never the one just made.
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Get-ChildItem -LiteralPath $env:CFG_BACKUP_ROOT -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}-\d{4}$' -and $_.FullName -ne $env:CFG_BACKUP } | Sort-Object Name -Descending | Select-Object -Skip ([int]$env:CFG_BACKUP_KEEP - 1) | Remove-Item -Recurse -Force" >nul 2>nul || echo [WARN] Could not prune old config backups in %CFG_BACKUP_ROOT%

dotnet publish "%MCP_PROJ%" -c %CONFIGURATION% -o "%PUBLISH_DIR%" --nologo
set "PUBLISH_RESULT=%errorlevel%"

rem Restore config: your backup first, the source default only on a fresh publish folder.
set "RESTORE_FAILED="
for %%F in (appsettings.json modes.json) do (
    if exist "%CFG_BACKUP%\%%F" (
        fc /b "%CFG_BACKUP%\%%F" "%PUBLISH_DIR%\%%F" >nul 2>nul || (
            copy /y "%CFG_BACKUP%\%%F" "%PUBLISH_DIR%\%%F" >nul && echo [Info] Restored your %%F from backup. || set "RESTORE_FAILED=1"
        )
    ) else if not exist "%PUBLISH_DIR%\%%F" (
        copy /y "%MCP_SRC%\%%F" "%PUBLISH_DIR%\%%F" >nul && echo [Info] %%F did not exist - copied the default from source. || set "RESTORE_FAILED=1"
    )
)
if defined RESTORE_FAILED (
    rem Paths are quoted: inside a ( ) block an unquoted ')' in a path (e.g. "Program Files (x86)") would close the block early.
    echo [ERROR] Could not restore the config files in "%PUBLISH_DIR%".
    echo         Copy them back by hand from: "%CFG_BACKUP%"
    exit /b 1
)
echo [Info] Config kept in place. Backup of this run: %CFG_BACKUP%

if not "%PUBLISH_RESULT%"=="0" (
    echo [ERROR] Publish failed. See the messages above.
    exit /b 1
)

echo [OK] Publish complete.

rem Tell which modes / roles / fields exist only in the source config. Files are not changed.
echo.
dotnet "%MCP_DLL%" --config-drift "%MCP_SRC%" "%PUBLISH_DIR%"
rem Not "if errorlevel 1": an unhandled .NET exception exits with 0xE0434352, which is negative and would skip the warning.
if not "%errorlevel%"=="0" echo [WARN] The config comparison did not finish for every file - see above. Publish itself succeeded.

if defined STOPPED (
    echo.
    echo [Next] The MCP server was stopped in your running Claude Code sessions.
    echo        Claude Code does not reconnect by itself. In each session:
    echo          1. Run /mcp and reconnect hb-orchestration.
    echo          2. Optional check: ask Claude to run server_info - it shows the new build time.
    echo        The dashboard Sessions page also shows each session's server build time.
) else (
    echo        Start Claude Code, or run /mcp and reconnect hb-orchestration, to load the new server.
)
exit /b 0

:backup_failed
echo [ERROR] Could not back up the live config to "%CFG_BACKUP%".
echo         Publish cancelled - nothing was changed in %PUBLISH_DIR%.
if defined STOPPED echo         The MCP server was stopped. Run /mcp in Claude Code to reconnect it.
exit /b 1


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
    echo [ERROR] Dashboard project not found. Check SOLUTION_DIR in %CONFIG_NAME%.
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
