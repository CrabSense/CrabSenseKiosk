@echo off
cd /d "%~dp0"
set "URLFILE=%~dp0be-url.txt"
set "BE_URL="
if exist "%URLFILE%" for /f "usebackq delims=" %%A in ("%URLFILE%") do set "BE_URL=%%A"

if "%BE_URL%"=="" goto asknew

:askchange
echo BE hien tai: %BE_URL%
set "CHANGE="
set /p CHANGE=Thay doi? (yes/no): 
if /i "%CHANGE%"=="yes" goto asknew
if /i "%CHANGE%"=="no" goto start
echo Chi nhap yes hoac no
goto askchange

:asknew
set "BE_URL="
set /p BE_URL=Nhap link BE: 
if "%BE_URL%"=="" (
  echo Chua nhap link BE
  exit /b 1
)
>"%URLFILE%" echo %BE_URL%

:start
netstat -ano | findstr ":8090" | findstr "LISTENING" >nul
if not errorlevel 1 (
  echo Kiosk da lang nghe cong 8090. Tat cua so do roi chay lai de dung link moi.
  exit /b 0
)
echo Bat Kiosk http://0.0.0.0:8090
echo BE: %BE_URL%
set "EdgeGateway__BackendUrl=%BE_URL%"
dotnet run --urls "http://0.0.0.0:8090" --no-launch-profile
