@echo off
title Sahyadri DeckLink Broadcaster (Release x64)
cd /d "%~dp0"

echo ============================================================
echo Starting Sahyadri DeckLink Broadcaster (.NET 10 x64 Release)
echo ============================================================

if exist "bin\Release\net10.0-windows\win-x64\DeckLinkStreamStudio.exe" (
    start "" "bin\Release\net10.0-windows\win-x64\DeckLinkStreamStudio.exe"
) else if exist "bin\Release\net10.0-windows\DeckLinkStreamStudio.exe" (
    start "" "bin\Release\net10.0-windows\DeckLinkStreamStudio.exe"
) else (
    echo Building Release x64 binary...
    dotnet build -c Release
    if exist "bin\Release\net10.0-windows\win-x64\DeckLinkStreamStudio.exe" (
        start "" "bin\Release\net10.0-windows\win-x64\DeckLinkStreamStudio.exe"
    )
)
