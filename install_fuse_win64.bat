@echo off
setlocal enabledelayedexpansion

REM Define variables
set "TEMP_PUBLISH_DIR=%~dp0temp_publish"
set "INSTALL_DIR=C:\Program Files\FuseTool"
set "TOOL_NAME=fuse.exe"
set "PROJECT_DIR=%~dp0src\Fuse.Cli"

REM Check for administrative privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo This script requires administrative privileges.
    echo Please run as administrator.
    pause
    exit /b 1
)

REM Ensure we are in the script's directory
cd /d "%~dp0"

REM Clean up any previous temporary publish directory
if exist "%TEMP_PUBLISH_DIR%" (
    echo Cleaning up previous temporary directory...
    rmdir /S /Q "%TEMP_PUBLISH_DIR%"
)

REM Publish the application as a single file to the temporary directory
echo Publishing the Fuse tool to a temporary directory...
dotnet publish "%PROJECT_DIR%" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o "%TEMP_PUBLISH_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo Publish failed. Exiting script.
    pause
    exit /b 1
)

REM Clean up previous installation if it exists
if exist "%INSTALL_DIR%" (
    echo Cleaning up previous installation...
    rmdir /S /Q "%INSTALL_DIR%"
)

REM Create installation directory
echo Creating installation directory...
mkdir "%INSTALL_DIR%"

REM Copy the published single executable to the installation directory
echo Copying executable to installation directory...
xcopy /Y "%TEMP_PUBLISH_DIR%\%TOOL_NAME%" "%INSTALL_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo File copy failed. Exiting script.
    pause
    exit /b 1
)

REM Add installation directory to PATH if not already present
echo Checking if the installation directory is in the system PATH...
for /f "tokens=2*" %%A in ('reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v PATH') do set "CURRENT_PATH=%%B"
echo %CURRENT_PATH% | findstr /I /C:"%INSTALL_DIR%" >nul
if %ERRORLEVEL% NEQ 0 (
    echo Adding the installation directory to system PATH...
    setx /M PATH "%INSTALL_DIR%;%CURRENT_PATH%"
    if %ERRORLEVEL% NEQ 0 (
        echo Failed to add directory to system PATH. Exiting script.
        pause
        exit /b 1
    )
) else (
    echo The installation directory is already in the system PATH.
)

REM Clean up the temporary publish directory
echo Cleaning up temporary files...
rmdir /S /Q "%TEMP_PUBLISH_DIR%"

echo Fuse tool has been installed successfully.
echo You may need to restart your command prompt to use the 'fuse' command.

endlocal
pause