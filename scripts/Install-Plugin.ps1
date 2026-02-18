<#
.SYNOPSIS
    Installs the Overtake Telemetry plugin into SimHub (manual method).

.DESCRIPTION
    Copies the plugin DLL directly to SimHub's installation folder.
    Use this when double-clicking the .simhubplugin file does not trigger the installer.

    Steps:
    1. Closes SimHub if running (prompts first)
    2. Copies the DLL to SimHub's folder
    3. Offers to start SimHub

.PARAMETER SimHubPath
    Path to SimHub's installation folder. Auto-detected if not specified.

.EXAMPLE
    .\Install-Plugin.ps1
    .\Install-Plugin.ps1 -SimHubPath "D:\Programs\SimHub"
#>
param(
    [string]$SimHubPath = ""
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Overtake Telemetry - Manual Install" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Find the DLL
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$dllPath = "$repoRoot\src\Overtake.SimHub.Plugin\bin\Release\Overtake.SimHub.Plugin.dll"

if (-not (Test-Path $dllPath)) {
    Write-Host "ERROR: Plugin DLL not found. Run Build-Package.ps1 first." -ForegroundColor Red
    Write-Host "  Expected: $dllPath" -ForegroundColor Gray
    exit 1
}

# Find SimHub
if ([string]::IsNullOrWhiteSpace($SimHubPath)) {
    $candidates = @(
        "C:\Program Files (x86)\SimHub",
        "C:\Program Files\SimHub",
        "D:\Program Files (x86)\SimHub",
        "D:\Program Files\SimHub",
        "D:\SimHub"
    )
    foreach ($c in $candidates) {
        if (Test-Path "$c\SimHubWPF.exe") {
            $SimHubPath = $c
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($SimHubPath) -or -not (Test-Path "$SimHubPath\SimHubWPF.exe")) {
    Write-Host "SimHub installation not found automatically." -ForegroundColor Yellow
    Write-Host ""
    $SimHubPath = Read-Host "Enter the full path to your SimHub folder (e.g., C:\Program Files (x86)\SimHub)"
    if (-not (Test-Path "$SimHubPath\SimHubWPF.exe")) {
        Write-Host "ERROR: SimHubWPF.exe not found in '$SimHubPath'" -ForegroundColor Red
        exit 1
    }
}

Write-Host "SimHub found: $SimHubPath" -ForegroundColor Green
Write-Host ""

# Check if SimHub is running
$simhubProcess = Get-Process "SimHubWPF" -ErrorAction SilentlyContinue
if ($simhubProcess) {
    Write-Host "SimHub is currently running. It must be closed to install the plugin." -ForegroundColor Yellow
    $answer = Read-Host "Close SimHub now? (Y/N)"
    if ($answer -match "^[Yy]") {
        Write-Host "Closing SimHub..." -ForegroundColor Yellow
        Stop-Process -Name "SimHubWPF" -Force
        Start-Sleep -Seconds 3
    } else {
        Write-Host "Please close SimHub manually and run this script again." -ForegroundColor Yellow
        exit 0
    }
}

# Copy DLL
$destPath = "$SimHubPath\Overtake.SimHub.Plugin.dll"
Write-Host "Copying plugin DLL..." -ForegroundColor Yellow
Copy-Item $dllPath $destPath -Force
Write-Host "  Installed: $destPath" -ForegroundColor Green

# Verify
if (Test-Path $destPath) {
    $size = (Get-Item $destPath).Length
    Write-Host "  Size: $([math]::Round($size/1024, 1)) KB" -ForegroundColor Gray
} else {
    Write-Host "ERROR: Copy failed!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  INSTALL COMPLETE" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Start SimHub" -ForegroundColor Gray
Write-Host "  2. Go to Settings > Plugins" -ForegroundColor Gray
Write-Host "  3. Enable 'Overtake Telemetry' if not already enabled" -ForegroundColor Gray
Write-Host "  4. In F1 25: Settings > Telemetry > UDP Port = 20778" -ForegroundColor Yellow
Write-Host ""

$startAnswer = Read-Host "Start SimHub now? (Y/N)"
if ($startAnswer -match "^[Yy]") {
    Start-Process "$SimHubPath\SimHubWPF.exe"
    Write-Host "SimHub starting..." -ForegroundColor Green
}

Write-Host ""
