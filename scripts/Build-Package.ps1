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
Write-Host "[1/4] Building Release..." -ForegroundColor Yellow
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
    Write-Host "[2/4] Running tests..." -ForegroundColor Yellow

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
    Write-Host "[2/4] Tests skipped" -ForegroundColor DarkYellow
}

# Step 3: Obfuscate DLL (optional, requires ConfuserEx CLI)
Write-Host ""
Write-Host "[3/5] Obfuscating DLL (ConfuserEx)..." -ForegroundColor Yellow

$confuserCli = $null
$confuserSearchPaths = @(
    "$repoRoot\tools\ConfuserEx\Confuser.CLI.exe",
    "C:\Tools\ConfuserEx\Confuser.CLI.exe",
    "$env:LOCALAPPDATA\ConfuserEx\Confuser.CLI.exe"
)
foreach ($cp in $confuserSearchPaths) {
    if (Test-Path $cp) { $confuserCli = $cp; break }
}

if ($confuserCli) {
    $confuserOut = "$binDir\Confused"
    if (Test-Path $confuserOut) { Remove-Item $confuserOut -Recurse -Force }

    $crprojTemplate = Get-Content "$repoRoot\confuser.crproj" -Raw
    $crprojContent = $crprojTemplate `
        -replace '\{outputDir\}', $confuserOut `
        -replace '\{baseDir\}', $binDir `
        -replace '\{dllPath\}', $dllName
    $crprojTmp = "$binDir\_confuser.crproj"
    Set-Content -Path $crprojTmp -Value $crprojContent -Encoding UTF8

    & $confuserCli $crprojTmp 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    $obfuscatedDll = "$confuserOut\$dllName"
    if (Test-Path $obfuscatedDll) {
        Copy-Item $obfuscatedDll $dllPath -Force
        Write-Host "  Obfuscated DLL applied" -ForegroundColor Green
    } else {
        Write-Host "  [WARN] Obfuscated DLL not found, using unobfuscated build" -ForegroundColor DarkYellow
    }
    Remove-Item $crprojTmp -Force -ErrorAction SilentlyContinue
    Remove-Item $confuserOut -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "  [SKIP] ConfuserEx CLI not found. Download to tools\ConfuserEx\ for obfuscation." -ForegroundColor DarkYellow
    Write-Host "  https://github.com/mkaring/ConfuserEx/releases" -ForegroundColor DarkGray
}

# Step 4: Build Inno Setup installer
Write-Host ""
Write-Host "[4/5] Building installer (Inno Setup)..." -ForegroundColor Yellow

# Prefer dist\v{AssemblyVersion}\installer.iss; else newest dist\v*\installer.iss by semver
$asmInfoPath = "$repoRoot\src\Overtake.SimHub.Plugin\Properties\AssemblyInfo.cs"
$asmShortVer = $null
if (Test-Path $asmInfoPath) {
    $asmRaw = Get-Content $asmInfoPath -Raw
    if ($asmRaw -match 'AssemblyVersion\("(\d+\.\d+\.\d+)') { $asmShortVer = $Matches[1] }
}
$preferredIss = if ($asmShortVer) { "$repoRoot\dist\v$asmShortVer\installer.iss" } else { $null }
if ($preferredIss -and (Test-Path $preferredIss)) {
    $issPath = $preferredIss
    $issDir = Split-Path $issPath -Parent
} else {
    $issRow = Get-ChildItem "$repoRoot\dist" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $iss = Join-Path $_.FullName "installer.iss"
        if (-not (Test-Path $iss)) { return }
        $dirName = $_.Name
        if ($dirName -match '^v([\d\.]+)$') {
            [PSCustomObject]@{ Dir = $_.FullName; Ver = [version]$Matches[1]; Iss = $iss }
        }
    } | Sort-Object Ver -Descending | Select-Object -First 1
    if (-not $issRow) {
        $issPath = "$repoRoot\dist\v1.1.12\installer.iss"
        $issDir = Split-Path $issPath -Parent
    } else {
        $issPath = $issRow.Iss
        $issDir = $issRow.Dir
    }
    if ($preferredIss -and -not (Test-Path $preferredIss) -and $issRow) {
        $t = Split-Path -Leaf $issRow.Dir
        Write-Host "  [NOTE] No dist\v$asmShortVer - using Inno template from $t" -ForegroundColor DarkYellow
    }
}

$isccPaths = @(
    "C:\InnoSetup6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $null
foreach ($p in $isccPaths) {
    if (Test-Path $p) { $iscc = $p; break }
}

$installerExe = $null
if ($iscc -and (Test-Path $issPath)) {
    $filesDir = "$issDir\files"
    New-Item -ItemType Directory -Force -Path $filesDir | Out-Null
    Copy-Item $dllPath "$filesDir\$dllName" -Force

    $issContent = Get-Content $issPath -Raw
    $semVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath).FileVersion -replace '\.0$', ''
    $issContent = $issContent -replace '#define MyAppVersion ".*?"', "#define MyAppVersion `"$semVer`""
    Set-Content -Path $issPath -Value $issContent -Encoding UTF8 -NoNewline

    & $iscc $issPath 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    $installerExe = Get-ChildItem $issDir -Filter "*.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "Setup" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($installerExe) {
        $instSize = [math]::Round($installerExe.Length / 1024, 1)
        Write-Host "  Installer: $($installerExe.Name) ($instSize KB)" -ForegroundColor Green
        Copy-Item $installerExe.FullName "$OutputDir\$($installerExe.Name)" -Force
    } else {
        Write-Host "  [WARN] Installer EXE not found after ISCC" -ForegroundColor DarkYellow
    }
} else {
    if (-not $iscc) { Write-Host "  [SKIP] Inno Setup not found" -ForegroundColor DarkYellow }
    if (-not (Test-Path $issPath)) { Write-Host "  [SKIP] installer.iss not found at $issPath" -ForegroundColor DarkYellow }
}

# Step 5: Package
Write-Host ""
Write-Host "[5/5] Packaging .simhubplugin..." -ForegroundColor Yellow

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
$installerName = if ($installerExe) { $installerExe.Name } else { "Overtake.SimHub.Plugin-v$semVer-Setup.exe" }
$versionJsonObj = [ordered]@{
    version      = $semVer
    released     = (Get-Date -Format "yyyy-MM-dd")
    download     = "https://racehub.overtakef1.com/downloads"
    installerUrl = "https://github.com/drakokot-oss/overtake-simhub-plugin/releases/download/v$semVer/$installerName"
    releaseNotes = ""
}
$versionJsonPath = "$repoRoot\version.json"
Set-Content -Path $versionJsonPath -Value ($versionJsonObj | ConvertTo-Json) -Encoding UTF8
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
if ($installerExe) {
    Write-Host "  $($installerExe.Name)" -ForegroundColor White
}
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Next: run .\Release.ps1 to push + create GitHub Release" -ForegroundColor Cyan
Write-Host ""
