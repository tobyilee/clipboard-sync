@echo off
rem Dev helper: stop the running ClipSync (avoids DLL lock during build), build Release, start it again.
rem Usage (from the repo root): windows\dev-run.cmd
taskkill /IM ClipSync.exe /F >nul 2>&1
dotnet build "%~dp0ClipSync.App" -c Release || exit /b 1
start "" "%~dp0ClipSync.App\bin\Release\net10.0-windows\ClipSync.exe"
