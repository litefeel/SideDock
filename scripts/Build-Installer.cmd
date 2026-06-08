@echo off
setlocal

set ROOT=%~dp0..
pushd "%ROOT%"

if not defined DOTNET_CLI_HOME set DOTNET_CLI_HOME=%CD%\.dotnet_home
if not defined NUGET_PACKAGES set NUGET_PACKAGES=%CD%\.nuget\packages

set RUNTIME=win-x64
if not "%~1"=="" set RUNTIME=%~1

set WIX_ARCH=x64
if /i "%RUNTIME%"=="win-x86" set WIX_ARCH=x86
if /i "%RUNTIME%"=="win-arm64" set WIX_ARCH=arm64

set PUBLISH_DIR=%CD%\artifacts\publish\SideDock\app
set MSI_DIR=%CD%\artifacts\installer
set MSI_PATH=%MSI_DIR%\SideDock-%RUNTIME%.msi

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if not exist "%MSI_DIR%" mkdir "%MSI_DIR%"

dotnet tool restore
if errorlevel 1 exit /b %errorlevel%

dotnet publish src\SideDock\SideDock.csproj -c Release -r %RUNTIME% -o "%PUBLISH_DIR%" --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishReadyToRun=true /p:DebugType=None /p:DebugSymbols=false
if errorlevel 1 exit /b %errorlevel%

dotnet wix build installer\SideDock.wxs -d PublishDir="%PUBLISH_DIR%" -arch %WIX_ARCH% -out "%MSI_PATH%"
if errorlevel 1 exit /b %errorlevel%

echo.
echo Built MSI:
echo   %MSI_PATH%

popd
endlocal
