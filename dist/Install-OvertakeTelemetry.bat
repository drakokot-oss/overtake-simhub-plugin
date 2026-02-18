<# :
@echo off
copy "%~f0" "%TEMP%\OvertakeInstall.ps1" > nul
start "" powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File "%TEMP%\OvertakeInstall.ps1" "%~dp0."
exit /b
#>

param([string]$SrcDir)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$dllName = "Overtake.SimHub.Plugin.dll"
$dllPath = Join-Path $SrcDir $dllName

# --- Step 1: Verify DLL exists ---
if (-not (Test-Path $dllPath)) {
    [System.Windows.Forms.MessageBox]::Show(
        "$dllName was not found.`n`nMake sure it is in the same folder as this installer:`n$SrcDir",
        "Overtake Telemetry", "OK", "Error") | Out-Null
    exit 1
}

# --- Step 2: Find SimHub ---
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

if (-not $shPath) {
    [System.Windows.Forms.MessageBox]::Show(
        "SimHub was not found automatically.`nPlease select your SimHub folder in the next dialog.",
        "Overtake Telemetry", "OK", "Information") | Out-Null

    $fbd = New-Object System.Windows.Forms.FolderBrowserDialog
    $fbd.Description = "Select the SimHub installation folder (where SimHubWPF.exe is)"
    if ($fbd.ShowDialog() -eq "OK") {
        if (Test-Path "$($fbd.SelectedPath)\SimHubWPF.exe") {
            $shPath = $fbd.SelectedPath
        } else {
            [System.Windows.Forms.MessageBox]::Show(
                "SimHubWPF.exe was not found in that folder.`nInstallation cancelled.",
                "Overtake Telemetry", "OK", "Error") | Out-Null
            exit 1
        }
    } else { exit 0 }
}

# --- Step 3: Confirm ---
$r = [System.Windows.Forms.MessageBox]::Show(
    "Install Overtake Telemetry plugin into SimHub?`n`nSimHub folder:`n$shPath",
    "Overtake Telemetry", "YesNo", "Question")
if ($r -ne "Yes") { exit 0 }

# --- Step 4: Close SimHub if running ---
$running = Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue
if ($running) {
    $rc = [System.Windows.Forms.MessageBox]::Show(
        "SimHub is currently running.`nIt must be closed before installing.`n`nClose SimHub now?",
        "Overtake Telemetry", "YesNo", "Warning")
    if ($rc -eq "Yes") {
        Stop-Process -Name "SimHubWPF" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    } else {
        [System.Windows.Forms.MessageBox]::Show(
            "Please close SimHub manually and run this installer again.",
            "Overtake Telemetry", "OK", "Information") | Out-Null
        exit 0
    }
}

# --- Step 5: Copy DLL ---
try {
    Copy-Item -Path $dllPath -Destination "$shPath\$dllName" -Force
} catch {
    $em = $_.Exception.Message
    if ($em -match "ccess" -or $em -match "denied") {
        $ra = [System.Windows.Forms.MessageBox]::Show(
            "Permission denied. The installer needs Administrator rights.`n`nRelaunch as Administrator?",
            "Overtake Telemetry", "YesNo", "Warning")
        if ($ra -eq "Yes") {
            $batPath = Join-Path $SrcDir "Install-OvertakeTelemetry.bat"
            Start-Process cmd.exe "/c `"$batPath`"" -Verb RunAs
        }
    } else {
        [System.Windows.Forms.MessageBox]::Show(
            "Installation failed:`n$em",
            "Overtake Telemetry", "OK", "Error") | Out-Null
    }
    exit 1
}

# --- Step 6: Success ---
$rs = [System.Windows.Forms.MessageBox]::Show(
    "Installation complete!`n`n" +
    "REQUIRED CONFIGURATION:`n`n" +
    "1)  F1 25 Game:`n" +
    "     Settings > Telemetry > UDP Port = 20778`n`n" +
    "2)  SimHub:`n" +
    "     Home > F1 25 > Game config > UDP Port = 20777`n`n" +
    "Start SimHub now?",
    "Overtake Telemetry - Installed!", "YesNo", "Information")

if ($rs -eq "Yes") {
    Start-Process "$shPath\SimHubWPF.exe"
}

# Cleanup temp file
Remove-Item $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
