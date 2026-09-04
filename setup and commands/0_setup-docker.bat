@echo off
setlocal
cd /d "%~dp0"

echo.
echo Azeroth Platform Docker setup
echo Checks this PC, then installs Docker if it is missing.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp00_setup-docker.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
    echo Docker setup exited with code %EXITCODE%.
    echo.
    pause
    exit /b %EXITCODE%
)

echo Done.
timeout /t 5
exit /b 0
