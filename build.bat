@echo off
rem Dynamic Island - Licencia MIT.
chcp 65001 >nul
title Dynamic Island - Compilar
cd /d "%~dp0"
setlocal
set "ROOT=%~dp0"

rem Quita la marca "descargado de internet" de los archivos del proyecto
rem (evita avisos de SmartScreen en los scripts y en el ejecutable que se genera).
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -Path '%ROOT%' -Recurse -File | Unblock-File" >nul 2>nul

rem ---------------------------------------------------------------
rem 1. SDK de .NET: se usa el instalado; si falta, se instala solo.
rem ---------------------------------------------------------------
set "DOTNET="
call :finddotnet
if defined DOTNET goto build

echo.
echo  No se encontro el SDK de .NET 8. Instalandolo (solo esta vez)...
where winget >nul 2>nul
if errorlevel 1 goto localsdk
winget install --id Microsoft.DotNet.SDK.8 -e --silent --accept-package-agreements --accept-source-agreements
call :finddotnet
if defined DOTNET goto build

:localsdk
rem Respaldo sin winget ni administrador: instalacion local dentro de la carpeta del proyecto.
echo  Descargando el SDK de .NET 8 en la carpeta .dotnet del proyecto...
powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol='Tls12'; Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%TEMP%\dotnet-install.ps1' -UseBasicParsing; & '%TEMP%\dotnet-install.ps1' -Channel 8.0 -InstallDir '%ROOT%.dotnet'"
call :finddotnet
if defined DOTNET goto build
goto nosdk

:build
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
echo.
echo  Compilando Dynamic Island (la primera vez tarda 1-2 minutos)...
echo.
"%DOTNET%" publish src\DynamicIsland.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if errorlevel 1 goto fail

rem ---------------------------------------------------------------
rem 2. Herramientas externas (se descargan una sola vez).
rem ---------------------------------------------------------------
if exist "dist\PresentMon.exe" goto pmdone
echo.
echo  Descargando Intel PresentMon (FPS de cualquier juego o app)...
powershell -NoProfile -ExecutionPolicy Bypass -File "tools-get-presentmon.ps1" -Destination "dist\PresentMon.exe"
:pmdone

if exist "dist\headsetcontrol.exe" goto hcdone
echo.
echo  Descargando HeadsetControl (bateria de audifonos inalambricos USB)...
powershell -NoProfile -ExecutionPolicy Bypass -File "tools-get-headsetcontrol.ps1" -Destination "dist\headsetcontrol.exe"
:hcdone

rem Controlador de sensores PawnIO (temperatura del CPU). Se instala una sola vez.
reg query "HKLM\SYSTEM\CurrentControlSet\Services\PawnIO" >nul 2>nul
if not errorlevel 1 goto pawndone
where winget >nul 2>nul
if errorlevel 1 goto pawndone
echo.
echo  Instalando el controlador de sensores PawnIO (temperatura del CPU)...
echo  Windows pedira permiso de administrador una sola vez.
winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements
:pawndone

powershell -NoProfile -Command "Get-ChildItem -Path '%ROOT%dist' -File | Unblock-File" >nul 2>nul

echo.
echo  Listo: dist\DynamicIsland.exe
echo  Para llevarla a otro PC copia toda la carpeta dist.
echo.
start "" "dist\DynamicIsland.exe"
pause
exit /b 0

rem Busca un SDK 8 o superior: carpeta local .dotnet, instalacion de Windows o PATH.
:finddotnet
set "DOTNET="
for %%D in ("%ROOT%.dotnet\dotnet.exe" "%ProgramFiles%\dotnet\dotnet.exe") do (
  if not defined DOTNET if exist "%%~D" (
    "%%~D" --list-sdks 2>nul | findstr /r /b "[89]\. 1[0-9]\." >nul && set "DOTNET=%%~D"
  )
)
if defined DOTNET exit /b 0
where dotnet >nul 2>nul
if errorlevel 1 exit /b 0
dotnet --list-sdks 2>nul | findstr /r /b "[89]\. 1[0-9]\." >nul && set "DOTNET=dotnet"
exit /b 0

:nosdk
echo.
echo  No se pudo instalar el SDK de .NET 8 automaticamente.
echo  Revisa tu conexion a internet o instalalo desde:
echo      https://dotnet.microsoft.com/download/dotnet/8.0
echo  y vuelve a ejecutar build.bat.
echo.
pause
exit /b 1

:fail
echo.
echo  Hubo un error al compilar. Copia el texto de arriba y compartelo para corregirlo.
pause
exit /b 1
