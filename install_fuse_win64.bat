@echo off
setlocal enabledelayedexpansion

:: Set console colors for better visibility
set "INFO=[92m"
set "WARN=[93m"
set "ERROR=[91m"
set "RESET=[0m"

:: Define variables
set "TEMP_PUBLISH_DIR=%~dp0temp_publish"
set "INSTALL_DIR=C:\Program Files\FuseTool"
set "TOOL_NAME=fuse.exe"
set "PROJECT_DIR=%~dp0src\Fuse.Cli"

:: Check for administrative privileges
echo %INFO%Checking for administrative privileges...%RESET%
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo %ERROR%This script requires administrative privileges.%RESET%
    echo %ERROR%Please run as administrator.%RESET%
    pause
    exit /b 1
)
echo %INFO%Administrative privileges verified%RESET%

:: Ensure we are in the script's directory
cd /d "%~dp0"
echo %INFO%Working directory: %~dp0%RESET%

:: Backup current PATH
for /f "tokens=2*" %%A in ('reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v PATH') do set "ORIGINAL_PATH=%%B"
echo %INFO%Current PATH backed up%RESET%

:: Clean up any previous temporary publish directory
if exist "%TEMP_PUBLISH_DIR%" (
    echo %INFO%Cleaning up previous temporary directory...%RESET%
    rmdir /S /Q "%TEMP_PUBLISH_DIR%" >nul 2>&1
)

:: Create temporary directory
mkdir "%TEMP_PUBLISH_DIR%" >nul 2>&1
echo %INFO%Created new temporary directory%RESET%

:: Publish the application
echo %INFO%Publishing the Fuse tool...%RESET%
dotnet publish "%PROJECT_DIR%" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o "%TEMP_PUBLISH_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo %ERROR%Publish failed.%RESET%
    goto :cleanup_and_exit
)
echo %INFO%Successfully published application%RESET%

:: Verify the published file exists
if not exist "%TEMP_PUBLISH_DIR%\%TOOL_NAME%" (
    echo %ERROR%Published executable not found.%RESET%
    goto :cleanup_and_exit
)

:: Create installation directory if it doesn't exist
if not exist "%INSTALL_DIR%" (
    echo %INFO%Creating installation directory...%RESET%
    mkdir "%INSTALL_DIR%" >nul 2>&1
    if !ERRORLEVEL! NEQ 0 (
        echo %ERROR%Failed to create installation directory.%RESET%
        goto :cleanup_and_exit
    )
)

:: Stop any running instances of the tool
echo %INFO%Checking for running instances...%RESET%
taskkill /F /IM "%TOOL_NAME%" >nul 2>&1

:: Copy the executable
echo %INFO%Installing Fuse tool...%RESET%
copy /Y "%TEMP_PUBLISH_DIR%\%TOOL_NAME%" "%INSTALL_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo %ERROR%Failed to copy executable.%RESET%
    goto :cleanup_and_exit
)

:: PATH Modification Section
echo.
echo %INFO%Checking current PATH...%RESET%
echo %INFO%Installation directory: %INSTALL_DIR%%RESET%

:: Check if path already exists (case-insensitive)
echo %ORIGINAL_PATH% | findstr /I /C:"%INSTALL_DIR%" >nul
if %ERRORLEVEL% NEQ 0 (
    echo %INFO%Adding to PATH...%RESET%
    
    :: Create new PATH value (prepend new directory)
    set "NEW_PATH=%INSTALL_DIR%;%ORIGINAL_PATH%"
    
    :: Show PATH modification details
    echo %INFO%Original PATH: %ORIGINAL_PATH%%RESET%
    echo %INFO%New PATH: %NEW_PATH%%RESET%
    
    :: Update the PATH
    reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v Path /t REG_EXPAND_SZ /d "%NEW_PATH%" /f
    if !ERRORLEVEL! NEQ 0 (
        echo %ERROR%Failed to update PATH. Rolling back...%RESET%
        reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v Path /t REG_EXPAND_SZ /d "%ORIGINAL_PATH%" /f
        goto :cleanup_and_exit
    )
    echo %INFO%PATH updated successfully%RESET%
) else (
    echo %WARN%Installation directory already exists in PATH%RESET%
    echo %INFO%No PATH modification needed%RESET%
)

:: Verify PATH update
echo.
echo %INFO%Verifying PATH update...%RESET%
for /f "tokens=2*" %%A in ('reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v PATH') do set "UPDATED_PATH=%%B"
echo %UPDATED_PATH% | findstr /I /C:"%INSTALL_DIR%" >nul
if %ERRORLEVEL% NEQ 0 (
    echo %ERROR%PATH verification failed. Rolling back...%RESET%
    reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v Path /t REG_EXPAND_SZ /d "%ORIGINAL_PATH%" /f
    goto :cleanup_and_exit
) else (
    echo %INFO%PATH verification successful%RESET%
)

:: Cleanup
:cleanup
echo.
echo %INFO%Cleaning up temporary files...%RESET%
rmdir /S /Q "%TEMP_PUBLISH_DIR%" >nul 2>&1

:: Success message
echo.
echo %INFO%Installation completed successfully!%RESET%
echo %INFO%Please restart your command prompt to use the 'fuse' command.%RESET%
goto :end

:cleanup_and_exit
:: Restore original PATH if update failed
echo.
echo %WARN%Installation failed, restoring original PATH...%RESET%
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v Path /t REG_EXPAND_SZ /d "%ORIGINAL_PATH%" /f
goto :cleanup

:end
echo.
echo %INFO%Press any key to exit...%RESET%
endlocal
pause