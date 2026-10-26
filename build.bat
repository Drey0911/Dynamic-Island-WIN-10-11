@echo off
rem Copyright (c) 2026 Andrey Mantilla Leon. Licencia MIT.
chcp 65001 >nul
title Dynamic Island - Compilar
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 goto nosdk
dotnet --list-sdks | findstr /b "8. 9. 10." >nul
if errorlevel 1 goto nosdk

echo.
echo  Compilando Dynamic Island (la primera vez tarda 1-2 minutos)...
echo.
dotnet publish src\DynamicIsland.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if errorlevel 1 goto fail

if exist "dist\PresentMon.exe" goto pmdone
echo.
echo  Descargando Intel PresentMon (FPS de cualquier juego o app)...
powershell -NoProfile -ExecutionPolicy Bypass -File "tools-get-presentmon.ps1" -Destination "dist\PresentMon.exe"
:pmdone

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

echo.
echo  Listo: dist\DynamicIsland.exe
echo  (Copia DynamicIsland.exe junto con PresentMon.exe si lo llevas a otro PC)
echo.
start "" "dist\DynamicIsland.exe"
pause
exit /b 0

:nosdk
echo.
echo  Falta el SDK de .NET 8. Instalalo con este comando y vuelve a ejecutar build.bat:
echo.
echo      winget install Microsoft.DotNet.SDK.8
echo.
echo  (o descargalo de https://dotnet.microsoft.com/download/dotnet/8.0)
echo.
pause
exit /b 1

:fail
echo.
echo  Hubo un error al compilar. Copia el texto de arriba y compartelo para corregirlo.
pause
exit /b 1
