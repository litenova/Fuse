@echo off
setlocal

echo [92mCleaning previous nupkg output...[0m
if exist ".\src\Fuse.Cli\nupkg" rd /s /q ".\src\Fuse.Cli\nupkg"

echo.
echo [92mBuilding and Packing Fuse...[0m
dotnet pack ".\src\Fuse.Cli\Fuse.Cli.csproj" -c Release -v q --nologo
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
dotnet tool install -g Fuse --add-source ".\src\Fuse.Cli\nupkg" -v q

if %ERRORLEVEL% NEQ 0 (
    echo [91mInstallation failed.[0m
    pause
    exit /b 1
)

echo.
echo [92mSuccess! Type 'fuse --help' to get started.[0m
pause