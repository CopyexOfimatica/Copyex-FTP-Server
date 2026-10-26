@echo off
rem Ejecuta las pruebas automaticas del motor FTP (requiere haber compilado una vez para tener .tools\dotnet).
setlocal
cd /d "%~dp0"
set "DOTNET=%~dp0.tools\dotnet\dotnet.exe"
if not exist "%DOTNET%" (
    echo Primero ejecuta COMPILAR_PORTABLE_X64.bat para descargar el SDK portable.
    pause
    exit /b 1
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
"%DOTNET%" run --project "tests\CopyexFTPServer.Tests\CopyexFTPServer.Tests.csproj" -c Release
pause
