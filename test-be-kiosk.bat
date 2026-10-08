@echo off
cd /d "%~dp0"
set "BE_URL="
if exist "%~dp0be-url.txt" for /f "usebackq delims=" %%A in ("%~dp0be-url.txt") do set "BE_URL=%%A"
if "%BE_URL%"=="" (
  echo Chua co link BE. Chay start-kiosk.bat truoc.
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-be-kiosk.ps1" -BeUrl "%BE_URL%"
exit /b %ERRORLEVEL%
