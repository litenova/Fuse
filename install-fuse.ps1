# install-fuse.ps1
# Installs fuse.exe to %LOCALAPPDATA%\Fuse and adds it to the user PATH.
# Run with: .\install-fuse.ps1
# No admin rights required.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Fuse'
$exeSource  = Join-Path $PSScriptRoot 'fuse.exe'
$exeDest    = Join-Path $installDir 'fuse.exe'

# 1. Create install directory if it doesn't exist
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir | Out-Null
    Write-Host "Created: $installDir"
}

# 2. Copy the executable
Copy-Item -Path $exeSource -Destination $exeDest -Force
Write-Host "Installed: $exeDest"

# 3. Add to user PATH if not already present
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
$pathEntries = $userPath -split ';' | Where-Object { $_ -ne '' }

if ($pathEntries -notcontains $installDir) {
    $newPath = ($pathEntries + $installDir) -join ';'
    [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
    Write-Host "Added to PATH: $installDir"
} else {
    Write-Host "Already in PATH: $installDir"
}

Write-Host ""
Write-Host "Fuse installed successfully."
Write-Host "Open a new terminal and run: fuse --help"
