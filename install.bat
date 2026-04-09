@echo off
setlocal

powershell -NoProfile -Command "Write-Host 'Cleaning previous nupkg output...' -ForegroundColor Cyan"
if exist ".\src\Fuse.Cli\nupkg" rd /s /q ".\src\Fuse.Cli\nupkg"

echo.
powershell -NoProfile -Command "Write-Host 'Building and packing Fuse...' -ForegroundColor Cyan"
dotnet pack ".\src\Fuse.Cli\Fuse.Cli.csproj" -c Release -v q --nologo
if %ERRORLEVEL% NEQ 0 (
    powershell -NoProfile -Command "Write-Host 'Build failed.' -ForegroundColor Red"
    pause
    exit /b 1
)

echo.
powershell -NoProfile -Command "Write-Host 'Uninstalling previous version (if any)...' -ForegroundColor Cyan"
dotnet tool uninstall -g Fuse 2>nul

echo.
powershell -NoProfile -Command "Write-Host 'Installing Fuse globally...' -ForegroundColor Cyan"
dotnet tool install -g Fuse --add-source ".\src\Fuse.Cli\nupkg" -v q

if %ERRORLEVEL% NEQ 0 (
    powershell -NoProfile -Command "Write-Host 'Installation failed.' -ForegroundColor Red"
    pause
    exit /b 1
)

echo.
powershell -NoProfile -Command "Write-Host 'Success! Type fuse --help to get started.' -ForegroundColor Green"
pause