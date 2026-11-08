@echo off
chcp 936 >nul
cd /d "%~dp0"

echo ============================================
echo   Build + IL string encryption + single file
echo ============================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack.ps1"
if errorlevel 1 goto fail

echo.
pause
exit /b 0

:fail
echo.
echo [FAILED] see messages above
pause
exit /b 1
