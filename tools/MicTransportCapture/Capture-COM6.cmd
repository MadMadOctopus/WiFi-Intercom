@echo off
setlocal
cd /d "%~dp0"
dotnet run --project MicTransportCapture.csproj -- --port COM6 --seconds 45 --output "%~dp0capture"
pause
