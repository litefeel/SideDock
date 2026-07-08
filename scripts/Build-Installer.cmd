@echo off
setlocal

set ROOT=%~dp0..
pushd "%ROOT%"

set VERSION=0.0.19
set RUNTIME=win-x64
set PACKAGE_KIND=with-runtime

if not "%~1"=="" set VERSION=%~1
if /i "%~1"=="win-x64" set RUNTIME=%~1
if /i "%~1"=="win-x86" set RUNTIME=%~1
if /i "%~1"=="win-arm64" set RUNTIME=%~1
if /i "%~1"=="win-x64" set VERSION=0.0.19
if /i "%~1"=="win-x86" set VERSION=0.0.19
if /i "%~1"=="win-arm64" set VERSION=0.0.19

if not defined DOTNET_CLI_HOME set DOTNET_CLI_HOME=%CD%\.dotnet_home
if not defined NUGET_PACKAGES set NUGET_PACKAGES=%CD%\.nuget\packages

if not "%~2"=="" set RUNTIME=%~2
if not "%~3"=="" set PACKAGE_KIND=%~3

set WIX_ARCH=x64
if /i "%RUNTIME%"=="win-x86" set WIX_ARCH=x86
if /i "%RUNTIME%"=="win-arm64" set WIX_ARCH=arm64

set SELF_CONTAINED=true
set PUBLISH_READY_TO_RUN=true
set ENABLE_COMPRESSION=true
set MSI_BASENAME=SideDock-%VERSION%-%RUNTIME%
if /i "%PACKAGE_KIND%"=="no-runtime" set SELF_CONTAINED=false
if /i "%PACKAGE_KIND%"=="no-runtime" set PUBLISH_READY_TO_RUN=false
if /i "%PACKAGE_KIND%"=="no-runtime" set ENABLE_COMPRESSION=false
if /i "%PACKAGE_KIND%"=="no-runtime" set MSI_BASENAME=SideDock-%VERSION%-%RUNTIME%-no-runtime
if /i "%PACKAGE_KIND%"=="framework-dependent" set SELF_CONTAINED=false
if /i "%PACKAGE_KIND%"=="framework-dependent" set PUBLISH_READY_TO_RUN=false
if /i "%PACKAGE_KIND%"=="framework-dependent" set ENABLE_COMPRESSION=false
if /i "%PACKAGE_KIND%"=="framework-dependent" set MSI_BASENAME=SideDock-%VERSION%-%RUNTIME%-no-runtime

set PUBLISH_ID=%VERSION%-%RUNTIME%-%PACKAGE_KIND%-%RANDOM%-%RANDOM%
set PUBLISH_DIR=%CD%\artifacts\publish\SideDock\%PUBLISH_ID%\app
set BUILD_OBJ_DIR=%TEMP%\SideDock-%VERSION%-%RUNTIME%-%RANDOM%-%RANDOM%-obj\
set BUILD_BIN_DIR=%TEMP%\SideDock-%VERSION%-%RUNTIME%-%RANDOM%-%RANDOM%-bin\
set MSI_DIR=%CD%\artifacts\installer
set MSI_PATH=%MSI_DIR%\%MSI_BASENAME%.msi

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if not exist "%MSI_DIR%" mkdir "%MSI_DIR%"

dotnet tool restore
if errorlevel 1 exit /b %errorlevel%

dotnet publish src\SideDock\SideDock.csproj -c Release -r %RUNTIME% -o "%PUBLISH_DIR%" --self-contained %SELF_CONTAINED% /p:Version=%VERSION% /p:AssemblyVersion=%VERSION%.0 /p:FileVersion=%VERSION%.0 /p:InformationalVersion=%VERSION% /p:BaseIntermediateOutputPath=%BUILD_OBJ_DIR% /p:BaseOutputPath=%BUILD_BIN_DIR% /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=%ENABLE_COMPRESSION% /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishReadyToRun=%PUBLISH_READY_TO_RUN% /p:DebugType=None /p:DebugSymbols=false
if errorlevel 1 exit /b %errorlevel%

dotnet wix build installer\SideDock.wxs -d ProductVersion=%VERSION% -d PublishDir="%PUBLISH_DIR%" -arch %WIX_ARCH% -out "%MSI_PATH%"
if errorlevel 1 exit /b %errorlevel%

echo.
echo Built MSI:
echo   %MSI_PATH%

popd
endlocal
