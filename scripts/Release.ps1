<#
.SYNOPSIS
    One-command release: build, test, package, commit, push, GitHub Release, install.

.DESCRIPTION
    Fully automated release pipeline for Overtake Telemetry plugin.

    1. Auto-bumps version (patch increment) or uses explicit version
    2. Extracts release notes from CHANGELOG.md
    3. Writes releaseNotes into version.json
    4. Builds the plugin (Release config) + Inno Setup installer
    5. Runs all tests (SessionStore + Finalizer)
    6. Git: commit + tag
    7. Git: push to GitHub (origin main + tags)
    8. Creates GitHub Release with installer attached (via gh CLI)
    9. Installs DLL into local SimHub (if found)

    After this script runs:
    - version.json on GitHub is updated -> plugin users see "Update available"
    - GitHub Release has the installer .exe -> website downloads page auto-updates
    - Local SimHub has the new DLL installed

.PARAMETER Version
    Explicit version (e.g. "1.2.0"). If omitted, auto-increments patch.

.PARAMETER SkipTests
    Skip running tests (faster, for hotfixes).

.PARAMETER NoPush
    Skip git push and GitHub Release (commit + tag locally only).

.PARAMETER NoInstall
    Skip installing the DLL into local SimHub.

.EXAMPLE
    .\Release.ps1                        # Auto-bump patch: 1.1.12 -> 1.1.13
    .\Release.ps1 -Version "2.0.0"       # Explicit version
    .\Release.ps1 -SkipTests             # Fast release (no tests)
    .\Release.ps1 -NoPush -NoInstall     # Build only, no deploy
#>
param(
    [string]$Version = "",
    [switch]$SkipTests,
    [switch]$NoPush,
    [switch]$NoInstall
)
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$assemblyInfoPath = "$repoRoot\src\Overtake.SimHub.Plugin\Properties\AssemblyInfo.cs"
$changelogPath = "$repoRoot\CHANGELOG.md"

# ── Step 0: Determine version ──
if ([string]::IsNullOrWhiteSpace($Version)) {
    $asmContent = Get-Content $assemblyInfoPath -Raw
    if ($asmContent -match 'AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.\d+"\)') {
        $major = [int]$Matches[1]
        $minor = [int]$Matches[2]
        $patch = [int]$Matches[3] + 1
        $Version = "$major.$minor.$patch"
    } else {
        Write-Host "ERROR: Could not read current version from AssemblyInfo.cs" -ForegroundColor Red
        exit 1
    }
    Write-Host ""
    Write-Host "  Auto-detected next version: $Version" -ForegroundColor DarkCyan
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Host "ERROR: Version must be X.Y.Z (e.g. 1.2.0)" -ForegroundColor Red
    exit 1
}

$assemblyVersion = "$Version.0"
$totalSteps = 8
if ($NoPush) { $totalSteps = 5 }
if ($NoPush -and $NoInstall) { $totalSteps = 4 }

Write-Host ""
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host "  OVERTAKE TELEMETRY - Automated Release v$Version" -ForegroundColor Magenta
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host ""

# ── Step 1: Bump version ──
Write-Host "[1/$totalSteps] Bumping version to $Version..." -ForegroundColor Yellow
$content = Get-Content $assemblyInfoPath -Raw
$content = $content -replace 'AssemblyVersion\("[^"]+"\)', "AssemblyVersion(`"$assemblyVersion`")"
$content = $content -replace 'AssemblyFileVersion\("[^"]+"\)', "AssemblyFileVersion(`"$assemblyVersion`")"
Set-Content -Path $assemblyInfoPath -Value $content -Encoding UTF8 -NoNewline
Write-Host "  AssemblyInfo.cs -> $assemblyVersion" -ForegroundColor Green

# ── Step 2: Extract release notes from CHANGELOG.md ──
Write-Host ""
Write-Host "[2/$totalSteps] Extracting release notes..." -ForegroundColor Yellow
$releaseNotes = ""
if (Test-Path $changelogPath) {
    $clContent = Get-Content $changelogPath -Raw
    $escapedVer = [regex]::Escape($Version)
    if ($clContent -match "(?ms)## \[$escapedVer\].*?\n(.*?)(?=\n## \[|\z)") {
        $releaseNotes = $Matches[1].Trim()
        $previewLines = ($releaseNotes -split "`n" | Select-Object -First 5) -join "`n"
        Write-Host "  Found notes for v$Version ($($releaseNotes.Length) chars)" -ForegroundColor Green
        Write-Host "  Preview:" -ForegroundColor DarkGray
        $previewLines -split "`n" | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    } else {
        Write-Host "  No entry for [$Version] in CHANGELOG.md" -ForegroundColor DarkYellow
        $releaseNotes = "Release v$Version"
    }
} else {
    Write-Host "  CHANGELOG.md not found - using default notes" -ForegroundColor DarkYellow
    $releaseNotes = "Release v$Version"
}

# ── Step 3: Build + Test + Package ──
Write-Host ""
Write-Host "[3/$totalSteps] Building, testing, packaging..." -ForegroundColor Yellow
$buildArgs = @("-ExecutionPolicy", "Bypass", "-File", "$PSScriptRoot\Build-Package.ps1")
if ($SkipTests) { $buildArgs += "-SkipTests" }
$buildOutput = & powershell @buildArgs 2>&1
$buildExit = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host "  $_" }
if ($buildExit -ne 0) {
    Write-Host ""
    Write-Host "BUILD FAILED. Release aborted." -ForegroundColor Red
    exit 1
}

# ── Step 4: Write release notes into version.json + verify artifacts ──
Write-Host ""
Write-Host "[4/$totalSteps] Verifying artifacts + writing release notes..." -ForegroundColor Yellow
$dllPath = "$repoRoot\dist\Overtake.SimHub.Plugin.dll"
$versionJsonPath = "$repoRoot\version.json"

$ok = $true
foreach ($artifact in @($dllPath, $versionJsonPath)) {
    if (Test-Path $artifact) {
        $size = [math]::Round((Get-Item $artifact).Length / 1024, 1)
        $aName = Split-Path $artifact -Leaf
        $msg = "  OK  $aName  $size KB"
        Write-Host $msg -ForegroundColor Green
    } else {
        $aName = Split-Path $artifact -Leaf
        $msg = "  MISSING  $aName"
        Write-Host $msg -ForegroundColor Red
        $ok = $false
    }
}

# Find the installer .exe (newest in dist/ — avoid picking an old *Setup*.exe)
$installerExe = Get-ChildItem "$repoRoot\dist" -Filter "*Setup*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($installerExe) {
    $instSize = [math]::Round($installerExe.Length / 1024, 1)
    $msg = "  OK  $($installerExe.Name)  $instSize KB"
    Write-Host $msg -ForegroundColor Green
} else {
    Write-Host "  WARN - No installer .exe found in dist/" -ForegroundColor DarkYellow
}

if (-not $ok) { Write-Host "Artifacts missing. Release aborted." -ForegroundColor Red; exit 1 }

# Inject releaseNotes into version.json
$vjContent = Get-Content $versionJsonPath -Raw | ConvertFrom-Json
$vjContent.releaseNotes = $releaseNotes
$installerName = if ($installerExe) { $installerExe.Name } else { "Overtake.SimHub.Plugin-v$Version-Setup.exe" }
$vjContent.installerUrl = "https://github.com/drakokot-oss/overtake-simhub-plugin/releases/download/v$Version/$installerName"
Set-Content -Path $versionJsonPath -Value ($vjContent | ConvertTo-Json -Depth 5) -Encoding UTF8
Write-Host "  version.json updated with releaseNotes + installerUrl" -ForegroundColor Green

# ── Step 5: Git commit + tag ──
Write-Host ""
Write-Host "[5/$totalSteps] Git commit + tag..." -ForegroundColor Yellow
$ErrorActionPreference = "Continue"
$gitStatus = & git -C $repoRoot status --porcelain 2>&1
if ($gitStatus) {
    & git -C $repoRoot add -A 2>&1 | Out-Null
    & git -C $repoRoot commit -m "release: v$Version" 2>&1 | Out-Null
    Write-Host "  Committed: release: v$Version" -ForegroundColor Green
} else {
    Write-Host "  No changes to commit" -ForegroundColor DarkYellow
}

$tagExists = & git -C $repoRoot tag -l "v$Version" 2>&1
if (-not $tagExists) {
    & git -C $repoRoot tag "v$Version" 2>&1 | Out-Null
    Write-Host "  Tagged: v$Version" -ForegroundColor Green
} else {
    Write-Host "  Tag v$Version already exists" -ForegroundColor DarkYellow
}
$ErrorActionPreference = "Stop"

if ($NoPush -and $NoInstall) {
    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Green
    Write-Host "  RELEASE v$Version READY (local only, not pushed)" -ForegroundColor Green
    Write-Host "================================================================" -ForegroundColor Green
    exit 0
}

# ── Step 6: Git push ──
$step = 6
if (-not $NoPush) {
    Write-Host ""
    Write-Host "[$step/$totalSteps] Pushing to GitHub..." -ForegroundColor Yellow
    $ErrorActionPreference = "Continue"
    & git -C $repoRoot push origin main --tags 2>&1 | ForEach-Object {
        $line = $_.ToString().Trim()
        if ($line) { Write-Host "  $line" -ForegroundColor DarkGray }
    }
    $pushExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($pushExit -ne 0) {
        Write-Host "  Push may have failed (exit $pushExit). Verify: git push origin main --tags" -ForegroundColor DarkYellow
    } else {
        Write-Host "  Pushed to origin/main + tag v$Version" -ForegroundColor Green
    }

    # Verify version.json is accessible on GitHub
    Write-Host "  Verifying version.json on GitHub..." -ForegroundColor DarkGray
    Start-Sleep -Seconds 3
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $remoteUrl = & git -C $repoRoot remote get-url origin 2>&1
        $repoPath = ""
        if ($remoteUrl -match "github\.com[:/](.+?)(?:\.git)?$") {
            $repoPath = $Matches[1]
        }
        if ($repoPath) {
            $rawUrl = "https://raw.githubusercontent.com/$repoPath/main/version.json"
            $remoteJson = (New-Object System.Net.WebClient).DownloadString($rawUrl) | ConvertFrom-Json
            if ($remoteJson.version -eq $Version) {
                Write-Host "  version.json on GitHub: v$($remoteJson.version)" -ForegroundColor Green
            } else {
                Write-Host "  version.json on GitHub shows v$($remoteJson.version) (may need cache refresh)" -ForegroundColor DarkYellow
            }
        }
    } catch {
        Write-Host "  Could not verify remote version.json (GitHub cache may need a few seconds)" -ForegroundColor DarkYellow
    }
    $step++

    # ── Step 7: Create GitHub Release ──
    Write-Host ""
    Write-Host "[$step/$totalSteps] Creating GitHub Release..." -ForegroundColor Yellow

    $ghAvailable = $null
    try { $ghAvailable = & gh --version 2>&1 | Select-Object -First 1 } catch {}

    if ($ghAvailable) {
        $releaseTitle = "Overtake Telemetry v$Version"
        $releaseArgs = @("release", "create", "v$Version", "--title", $releaseTitle, "--notes", $releaseNotes, "--repo", "drakokot-oss/overtake-simhub-plugin")
        if ($installerExe) {
            $releaseArgs += $installerExe.FullName
        }

        $ErrorActionPreference = "Continue"
        & gh @releaseArgs 2>&1 | ForEach-Object {
            $line = $_.ToString().Trim()
            if ($line) { Write-Host "  $line" -ForegroundColor DarkGray }
        }
        $ghExit = $LASTEXITCODE
        $ErrorActionPreference = "Stop"

        if ($ghExit -eq 0) {
            Write-Host "  GitHub Release v$Version created" -ForegroundColor Green
            if ($installerExe) {
                Write-Host "  Installer uploaded: $($installerExe.Name)" -ForegroundColor Green
            }
        } else {
            Write-Host "  GitHub Release may have failed (exit $ghExit)" -ForegroundColor DarkYellow
            Write-Host "  Create manually: gh release create v$Version --title `"$releaseTitle`" $($installerExe.FullName)" -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "  [SKIP] gh CLI not found. Install with: winget install GitHub.cli" -ForegroundColor DarkYellow
        Write-Host "  Then run: gh auth login" -ForegroundColor DarkYellow
        Write-Host "  Manual: gh release create v$Version --title `"Overtake Telemetry v$Version`" $($installerExe.FullName)" -ForegroundColor Gray
    }
    $step++
}

# ── Step 8: Install into local SimHub ──
if (-not $NoInstall) {
    Write-Host ""
    Write-Host "[$step/$totalSteps] Installing into local SimHub..." -ForegroundColor Yellow
    $shPath = $null
    $candidates = @(
        "${env:ProgramFiles(x86)}\SimHub",
        "$env:ProgramFiles\SimHub",
        "D:\Program Files (x86)\SimHub",
        "D:\Program Files\SimHub",
        "D:\SimHub",
        "C:\SimHub"
    )
    foreach ($c in $candidates) {
        if (Test-Path "$c\SimHubWPF.exe") { $shPath = $c; break }
    }

    if ($shPath) {
        $destDll = "$shPath\Overtake.SimHub.Plugin.dll"
        $simhubRunning = Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue
        if ($simhubRunning) {
            Write-Host "  SimHub is running. Stopping..." -ForegroundColor DarkYellow
            Stop-Process -Name "SimHubWPF" -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3
        }
        try {
            Copy-Item $dllPath $destDll -Force
            Write-Host "  Installed: $destDll" -ForegroundColor Green
            if ($simhubRunning) {
                Write-Host "  Restarting SimHub..." -ForegroundColor DarkGray
                Start-Process "$shPath\SimHubWPF.exe"
                Write-Host "  SimHub restarted" -ForegroundColor Green
            }
        } catch {
            Write-Host "  Install failed: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "  Copy manually: $dllPath -> $destDll" -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "  SimHub not found. Copy manually:" -ForegroundColor DarkYellow
        Write-Host "  $dllPath -> [SimHub folder]\Overtake.SimHub.Plugin.dll" -ForegroundColor Gray
    }
    $step++
}

# ── Summary ──
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "  RELEASE v$Version COMPLETE" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  What happened:" -ForegroundColor White
Write-Host "    1. Version bumped to $Version" -ForegroundColor Gray
Write-Host "    2. Release notes extracted from CHANGELOG.md" -ForegroundColor Gray
Write-Host "    3. Plugin built + tested + packaged + installer" -ForegroundColor Gray
Write-Host "    4. version.json updated (releaseNotes + installerUrl)" -ForegroundColor Gray
Write-Host "    5. Git commit + tag v$Version" -ForegroundColor Gray
if (-not $NoPush) {
    Write-Host "    6. Pushed to GitHub (users will see update notification)" -ForegroundColor Gray
    Write-Host "    7. GitHub Release created (website auto-updates)" -ForegroundColor Gray
}
if (-not $NoInstall) {
    Write-Host "    8. Installed in local SimHub" -ForegroundColor Gray
}
Write-Host ""
Write-Host "  Plugin users: update notification appears automatically" -ForegroundColor Cyan
Write-Host "  Website:      racehub.overtakef1.com/downloads" -ForegroundColor Cyan
Write-Host ""
