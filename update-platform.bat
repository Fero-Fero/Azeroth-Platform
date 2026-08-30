@echo off
setlocal
cd /d "%~dp0"

echo.
echo Azeroth Platform update
echo This pulls the latest main branch from GitHub.
echo Git for Windows is not required.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0update-platform.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
    echo Update exited with code %EXITCODE%.
    echo.
    pause
    exit /b %EXITCODE%
)

echo Done.
timeout /t 5
exit /b 0
