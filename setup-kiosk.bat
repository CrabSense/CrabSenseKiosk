@echo off
cd /d "%~dp0"
set "BE_URL="
if exist "%~dp0be-url.txt" for /f "usebackq delims=" %%A in ("%~dp0be-url.txt") do set "BE_URL=%%A"
if "%BE_URL%"=="" goto askbe

echo BE hien tai: %BE_URL%
set "CHANGE="
set /p CHANGE=Thay doi? (yes/no): 
if /i "%CHANGE%"=="yes" goto askbe
if /i "%CHANGE%"=="no" goto askcode
echo Chi nhap yes hoac no
exit /b 1

:askbe
set "BE_URL="
set /p BE_URL=Nhap link BE: 
if "%BE_URL%"=="" (
  echo Chua nhap link BE
  exit /b 1
)
>"%~dp0be-url.txt" echo %BE_URL%

:askcode
set "CODE="
set /p CODE=Nhap provisioning code: 
if "%CODE%"=="" (
  echo Chua nhap code
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-kiosk.ps1" -BeUrl "%BE_URL%" -Code "%CODE%"
exit /b %ERRORLEVEL%
