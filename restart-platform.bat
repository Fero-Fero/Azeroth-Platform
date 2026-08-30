@echo off
setlocal
cd /d "%~dp0"

echo.
echo Azeroth Platform restart
echo This rebuilds and starts the platform with docker compose up -d --build.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0restart-platform.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
    echo Restart exited with code %EXITCODE%.
    echo.
    pause
    exit /b %EXITCODE%
)

echo Done.
timeout /t 5
exit /b 0
