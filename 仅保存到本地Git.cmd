@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Git\Save-Project.ps1" -Mode Save
set "SAVE_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %SAVE_EXIT%
