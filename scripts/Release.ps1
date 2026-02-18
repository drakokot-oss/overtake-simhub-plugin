<#
.SYNOPSIS
    Bumps version, builds, tests, packages, and prepares a release.

.DESCRIPTION
    1. Bumps the version in AssemblyInfo.cs
    2. Runs Build-Package.ps1 (build + test + package + version.json)
    3. Creates a git commit and tag
    4. Shows instructions for pushing to GitHub

.PARAMETER Version
    The new version (e.g. "1.1.0"). Required.

.PARAMETER SkipTests
    Skip running tests.

.PARAMETER NoPush
    Skip the git push prompt.

.EXAMPLE
    .\Release.ps1 -Version "1.1.0"
    .\Release.ps1 -Version "1.2.0" -SkipTests
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$SkipTests,
    [switch]$NoPush
)
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path

# Validate version format
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Host "ERROR: Version must be in format X.Y.Z (e.g. 1.1.0)" -ForegroundColor Red
    exit 1
}

$assemblyVersion = "$Version.0"

Write-Host ""
Write-Host "========================================" -ForegroundColor Magenta
Write-Host "  Overtake Telemetry - Release v$Version" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta
Write-Host ""

# Step 1: Bump AssemblyInfo.cs
Write-Host "[1/4] Bumping version to $Version..." -ForegroundColor Yellow
$assemblyInfoPath = "$repoRoot\src\Overtake.SimHub.Plugin\Properties\AssemblyInfo.cs"
$content = Get-Content $assemblyInfoPath -Raw
$content = $content -replace 'AssemblyVersion\("[^"]+"\)', "AssemblyVersion(`"$assemblyVersion`")"
$content = $content -replace 'AssemblyFileVersion\("[^"]+"\)', "AssemblyFileVersion(`"$assemblyVersion`")"
Set-Content -Path $assemblyInfoPath -Value $content -Encoding UTF8 -NoNewline
Write-Host "  AssemblyInfo.cs updated to $assemblyVersion" -ForegroundColor Green

# Step 2: Build + Test + Package (this also generates version.json)
Write-Host ""
Write-Host "[2/4] Building and packaging..." -ForegroundColor Yellow
$buildArgs = @("-ExecutionPolicy", "Bypass", "-File", "$PSScriptRoot\Build-Package.ps1")
if ($SkipTests) { $buildArgs += "-SkipTests" }
$buildOutput = & powershell @buildArgs 2>&1
$buildExit = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host "  $_" }
if ($buildExit -ne 0) {
    Write-Host "BUILD FAILED. Release aborted." -ForegroundColor Red
    exit 1
}

# Step 3: Git commit + tag
Write-Host ""
Write-Host "[3/4] Creating git commit and tag..." -ForegroundColor Yellow
$gitStatus = & git -C $repoRoot status --porcelain 2>&1
if ($gitStatus) {
    & git -C $repoRoot add -A
    & git -C $repoRoot commit -m "release: v$Version"
    Write-Host "  Committed: release: v$Version" -ForegroundColor Green
} else {
    Write-Host "  No changes to commit" -ForegroundColor DarkYellow
}

$tagExists = & git -C $repoRoot tag -l "v$Version" 2>&1
if (-not $tagExists) {
    & git -C $repoRoot tag "v$Version"
    Write-Host "  Tagged: v$Version" -ForegroundColor Green
} else {
    Write-Host "  Tag v$Version already exists" -ForegroundColor DarkYellow
}

# Step 4: Push instructions
Write-Host ""
Write-Host "[4/4] Release ready!" -ForegroundColor Yellow
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  RELEASE v$Version READY" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Files in dist/:" -ForegroundColor White
Get-ChildItem "$repoRoot\dist" | ForEach-Object {
    Write-Host "    $($_.Name)  ($([math]::Round($_.Length/1024, 1)) KB)" -ForegroundColor Gray
}
Write-Host ""
Write-Host "  version.json updated (for auto-update checker)" -ForegroundColor Gray
Write-Host ""

if (-not $NoPush) {
    Write-Host "  To publish this release:" -ForegroundColor White
    Write-Host "    git push origin main --tags" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Then upload to racehub.overtakef1.com/downloads:" -ForegroundColor White
    Write-Host "    - OvertakeTelemetry-*.simhubplugin" -ForegroundColor Gray
    Write-Host "    - Install-OvertakeTelemetry.bat + Overtake.SimHub.Plugin.dll" -ForegroundColor Gray
    Write-Host ""
}
