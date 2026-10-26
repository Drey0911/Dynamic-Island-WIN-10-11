@echo off
rem Copyright (c) 2026 Andrey Mantilla Leon. Licencia MIT.
chcp 65001 >nul
cd /d "%~dp0"
rem Ejecuta en modo desarrollo (recompila al vuelo). Cierra la isla desde el icono de la bandeja.
dotnet run --project src\DynamicIsland.csproj
pause
