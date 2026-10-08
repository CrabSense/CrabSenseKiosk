@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-kiosk.ps1" -Status
set "ST=%ERRORLEVEL%"
echo.
set "BE_URL="
if exist "%~dp0be-url.txt" for /f "usebackq delims=" %%A in ("%~dp0be-url.txt") do set "BE_URL=%%A"
echo Step 1. Backend URL
echo Current URL: %BE_URL%
set "NEW="
set /p NEW=Enter a new URL, or press Enter to keep the current one: 
if not "%NEW%"=="" set "BE_URL=%NEW%"
echo %BE_URL% | findstr /i /r "^http://" >nul
if errorlevel 1 (
  echo %BE_URL% | findstr /i /r "^https://" >nul
  if errorlevel 1 (
    echo The URL must start with http:// or https://.
    exit /b 1
  )
)
>"%~dp0be-url.txt" echo %BE_URL%
echo.
echo Step 2. Provisioning code
set "CODE="
set /p CODE=Enter provisioning code: 
if "%CODE%"=="" goto emptycode
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-kiosk.ps1" -BeUrl "%BE_URL%" -Code "%CODE%"
if errorlevel 1 exit /b 1
goto listen

:emptycode
if not "%ST%"=="0" (
  echo Enter a provisioning code. The previous credential was revoked.
  exit /b 1
)

:listen
netstat -ano | findstr ":8090" | findstr "LISTENING" >nul
if not errorlevel 1 (
  echo Kiosk is already listening on port 8090. Close that window, then run this again.
  exit /b 0
)
echo Starting Kiosk at http://0.0.0.0:8090
echo Backend: %BE_URL%
set "EdgeGateway__BackendUrl=%BE_URL%"
dotnet run --urls "http://0.0.0.0:8090" --no-launch-profile
