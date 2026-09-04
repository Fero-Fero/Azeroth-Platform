@echo off
setlocal
cd /d "%~dp0"

echo.
echo Azeroth Platform - open manager
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0open-manager.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
    echo Open manager exited with code %EXITCODE%.
    echo.
    pause
    exit /b %EXITCODE%
)

echo Opened in your default browser.
timeout /t 3
exit /b 0
