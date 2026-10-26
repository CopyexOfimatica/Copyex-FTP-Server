@echo off
rem Uso interno: _BUILD_PORTABLE.bat [win-x64|win-x86] [carpeta_salida]
setlocal EnableExtensions
cd /d "%~dp0"

set "RID=%~1"
set "OUTDIR=%~2"
if "%RID%"=="" set "RID=win-x64"
if "%OUTDIR%"=="" set "OUTDIR=bin\Portable\%RID%"

set "PROJECT=src\CopyexFTPServer\CopyexFTPServer.csproj"
set "TOOLS_DIR=%~dp0.tools"
set "SDK_DIR=%TOOLS_DIR%\dotnet"
set "DOTNET=%SDK_DIR%\dotnet.exe"
set "INSTALL_SCRIPT=%TOOLS_DIR%\dotnet-install.ps1"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set NUGET_XMLDOC_MODE=skip

if not exist "%TOOLS_DIR%" mkdir "%TOOLS_DIR%" >nul 2>&1

if not exist "%DOTNET%" (
    echo.
    echo No se instala nada en Windows. Se descarga una copia PORTABLE del SDK .NET 10 en:
    echo   %SDK_DIR%
    echo.
    "%PS%" -NoProfile -ExecutionPolicy Bypass -Command ^
      "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%INSTALL_SCRIPT%'"
    if errorlevel 1 goto :sdk_error

    "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%INSTALL_SCRIPT%" -Channel 10.0 -Quality GA -InstallDir "%SDK_DIR%" -NoPath
    if errorlevel 1 goto :sdk_error
)
if not exist "%DOTNET%" goto :sdk_error

set "DOTNET_ROOT=%SDK_DIR%"
set "PATH=%SDK_DIR%;%PATH%"

echo.
echo SDK local:
"%DOTNET%" --version
if errorlevel 1 goto :sdk_error

echo.
echo Generando EXE portable autocontenido %RID%...
"%DOTNET%" publish "%PROJECT%" -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:PublishTrimmed=false ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o "%OUTDIR%"
if errorlevel 1 goto :build_error
if not exist "%OUTDIR%\CopyexFTPServer.exe" goto :build_error

echo.
echo ============================================================
echo  CORRECTO - NO NECESITA .NET INSTALADO EN EL PC DESTINO
echo ============================================================
echo EXE generado:
echo   %OUTDIR%\CopyexFTPServer.exe
echo.
echo Copia SOLO ese EXE a una carpeta del PC destino. Si en esa carpeta
echo hay un config.json de la version 2.x, se reutiliza (usuarios incluidos).
exit /b 0

:sdk_error
echo.
echo ERROR preparando el SDK portable (.tools\dotnet). Comprueba la conexion a Internet.
exit /b 2

:build_error
echo.
echo ERROR compilando Copyex FTP Server. Revisa el mensaje mostrado encima.
exit /b 3
