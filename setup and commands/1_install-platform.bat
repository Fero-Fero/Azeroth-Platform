@echo off
setlocal
cd /d "%~dp0"

echo.
echo Azeroth Platform Windows installer
echo This installs Docker Desktop if needed, then builds the platform.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp01_install-platform.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
    echo Installer exited with code %EXITCODE%.
    echo.
    pause
    exit /b %EXITCODE%
)

echo Done.
timeout /t 5
exit /b 0
