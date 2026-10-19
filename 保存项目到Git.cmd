@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Git\Save-Project.ps1"
set "SAVE_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %SAVE_EXIT%
