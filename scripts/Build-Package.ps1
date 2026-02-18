<#
.SYNOPSIS
    Builds the Overtake SimHub Plugin and creates a .simhubplugin installer package.

.DESCRIPTION
    1. Compiles the project in Release configuration
    2. Runs all tests (SessionStore + Finalizer)
    3. Packages the DLL into a .simhubplugin file (ZIP renamed)

    The .simhubplugin file can be double-clicked to install into SimHub.

.PARAMETER SkipTests
    Skip running tests after build.

.PARAMETER OutputDir
    Where to place the .simhubplugin file. Defaults to dist/ in the repo root.

.EXAMPLE
    .\Build-Package.ps1
    .\Build-Package.ps1 -SkipTests
    .\Build-Package.ps1 -OutputDir "C:\temp"
#>
param(
    [switch]$SkipTests,
    [string]$OutputDir = ""
)
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$projFile = "$repoRoot\src\Overtake.SimHub.Plugin\Overtake.SimHub.Plugin.csproj"
$binDir = "$repoRoot\src\Overtake.SimHub.Plugin\bin\Release"
$dllName = "Overtake.SimHub.Plugin.dll"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = "$repoRoot\dist"
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Overtake SimHub Plugin - Build & Pack" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Build
Write-Host "[1/3] Building Release..." -ForegroundColor Yellow
$msbuild = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    Write-Host "ERROR: MSBuild not found at $msbuild" -ForegroundColor Red
    exit 1
}

$buildOutput = & $msbuild $projFile /p:Configuration=Release /verbosity:minimal 2>&1
$buildExitCode = $LASTEXITCODE

# The PostBuildEvent copies to SimHub folder, which fails on machines without SimHub.
# We only care if the DLL was produced — filter out PostBuild XCOPY errors (MSB3073).
$realErrors = $buildOutput | Select-String ": error " | Where-Object { $_ -notmatch "MSB3073" }
if ($realErrors.Count -gt 0) {
    Write-Host "BUILD FAILED:" -ForegroundColor Red
    $buildOutput | ForEach-Object { Write-Host $_ }
    exit 1
}

$dllPath = "$binDir\$dllName"
if (-not (Test-Path $dllPath)) {
    Write-Host "BUILD FAILED: DLL not found at $dllPath" -ForegroundColor Red
    exit 1
}
Write-Host "  Build OK" -ForegroundColor Green

if ($buildExitCode -ne 0) {
    Write-Host "  (PostBuild copy to SimHub skipped - SimHub not found or locked)" -ForegroundColor DarkYellow
}

$dllSize = (Get-Item $dllPath).Length
Write-Host "  DLL: $dllName ($([math]::Round($dllSize/1024, 1)) KB)" -ForegroundColor Gray

# Step 2: Tests
if (-not $SkipTests) {
    Write-Host ""
    Write-Host "[2/3] Running tests..." -ForegroundColor Yellow

    $test1 = & powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\Test-SessionStore.ps1" -DllPath $dllPath 2>&1
    $t1Exit = $LASTEXITCODE
    $t1Summary = $test1 | Select-String "PASS:"
    if ($t1Exit -ne 0) {
        Write-Host "  SessionStore tests FAILED" -ForegroundColor Red
        $test1 | Where-Object { $_ -match "FAIL:" } | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host "  SessionStore: $t1Summary" -ForegroundColor Green

    $test2 = & powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\Test-Finalizer.ps1" -DllPath $dllPath 2>&1
    $t2Exit = $LASTEXITCODE
    $t2Summary = $test2 | Select-String "PASS:"
    if ($t2Exit -ne 0) {
        Write-Host "  Finalizer tests FAILED" -ForegroundColor Red
        $test2 | Where-Object { $_ -match "FAIL:" } | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host "  Finalizer:    $t2Summary" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[2/3] Tests skipped" -ForegroundColor DarkYellow
}

# Step 3: Package
Write-Host ""
Write-Host "[3/3] Packaging .simhubplugin..." -ForegroundColor Yellow

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath).FileVersion
if ([string]::IsNullOrWhiteSpace($version)) { $version = "1.0.0" }
$timestamp = Get-Date -Format "yyyyMMdd"
$packageName = "OvertakeTelemetry-$timestamp"
$zipPath = "$OutputDir\$packageName.zip"
$pluginPath = "$OutputDir\$packageName.simhubplugin"

if (Test-Path $pluginPath) { Remove-Item $pluginPath -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

$stagingDir = "$OutputDir\_staging"
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
Copy-Item $dllPath "$stagingDir\$dllName"

Add-Type -Assembly "System.IO.Compression.FileSystem"
[System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDir, $zipPath)
Rename-Item $zipPath $pluginPath

Remove-Item $stagingDir -Recurse -Force

$pkgSize = (Get-Item $pluginPath).Length
Write-Host "  Package: $packageName.simhubplugin ($([math]::Round($pkgSize/1024, 1)) KB)" -ForegroundColor Green

# Also copy raw DLL to dist/ for the bat installer
Copy-Item $dllPath "$OutputDir\Overtake.SimHub.Plugin.dll" -Force
Write-Host "  DLL copied to dist/ for manual install" -ForegroundColor Gray

# Verify installer bat exists in dist/
$batPath = "$repoRoot\dist\Install-OvertakeTelemetry.bat"
if (-not (Test-Path $batPath)) {
    Write-Host "  [WARN] Install-OvertakeTelemetry.bat not found in dist/" -ForegroundColor DarkYellow
} else {
    Write-Host "  Installer: Install-OvertakeTelemetry.bat" -ForegroundColor Gray
}

# Auto-generate version.json for update checker
$semVer = $version -replace '\.0$', ''
$versionJson = @{
    version  = $semVer
    download = "https://racehub.overtakef1.com/downloads"
    released = (Get-Date -Format "yyyy-MM-dd")
} | ConvertTo-Json
$versionJsonPath = "$repoRoot\version.json"
Set-Content -Path $versionJsonPath -Value $versionJson -Encoding UTF8
Write-Host "  version.json: v$semVer" -ForegroundColor Gray

# Create distribution ZIP for website download
$distZipName = "OvertakeTelemetry-v$semVer.zip"
$distZipPath = "$OutputDir\$distZipName"
if (Test-Path $distZipPath) { Remove-Item $distZipPath -Force }

$distStaging = "$OutputDir\_dist_staging"
if (Test-Path $distStaging) { Remove-Item $distStaging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distStaging | Out-Null

Copy-Item "$OutputDir\Overtake.SimHub.Plugin.dll" "$distStaging\Overtake.SimHub.Plugin.dll"
Copy-Item "$OutputDir\Install-OvertakeTelemetry.bat" "$distStaging\Install-OvertakeTelemetry.bat"
foreach ($readmeFile in @("README.txt", "LEIAME.txt")) {
    $rmSrc = "$OutputDir\$readmeFile"
    if (Test-Path $rmSrc) {
        Copy-Item $rmSrc "$distStaging\$readmeFile"
    }
}

[System.IO.Compression.ZipFile]::CreateFromDirectory($distStaging, $distZipPath)
Remove-Item $distStaging -Recurse -Force

$distZipSize = (Get-Item $distZipPath).Length
Write-Host "  Distribution ZIP: $distZipName ($([math]::Round($distZipSize/1024, 1)) KB)" -ForegroundColor Green

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  BUILD COMPLETE  (v$semVer)" -ForegroundColor Green
Write-Host "  $pluginPath" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "For racehub.overtakef1.com/downloads:" -ForegroundColor White
Write-Host "  Upload: $distZipName" -ForegroundColor Cyan
Write-Host ""
Write-Host "Users download the ZIP, extract, and double-click the .bat" -ForegroundColor Gray
Write-Host ""
