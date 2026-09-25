@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "DevProfiler\DevProfiler.csproj"
endlocal
