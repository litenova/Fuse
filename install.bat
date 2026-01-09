@echo off
setlocal

echo [92mBuilding and Packing Fuse...[0m
dotnet pack ".\src\Fuse.Cli\Fuse.Cli.csproj" -c Release
if %ERRORLEVEL% NEQ 0 (
    echo [91mBuild failed.[0m
    pause
    exit /b 1
)

echo.
echo [92mUninstalling previous version (if any)...[0m
dotnet tool uninstall -g Fuse 2>nul

echo.
echo [92mInstalling Fuse globally...[0m
dotnet tool install -g Fuse --add-source ".\src\Fuse.Cli\nupkg"

if %ERRORLEVEL% NEQ 0 (
    echo [91mInstallation failed.[0m
    pause
    exit /b 1
)

echo.
echo [92mSuccess! Type 'fuse --help' to get started.[0m
pause