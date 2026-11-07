@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo Falta o .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0 & pause & exit /b 1)
dotnet publish -c Release -o publicar || (pause & exit /b 1)
echo.
echo Feito! O AllyHub.exe esta na pasta "publicar".
pause
