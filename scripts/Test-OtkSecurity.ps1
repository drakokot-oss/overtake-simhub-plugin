<#
.SYNOPSIS
    Validates the OTK encryption/decryption round-trip preserves data integrity.
.DESCRIPTION
    1. Loads the plugin DLL
    2. Encrypts a test JSON payload with OtkWriter
    3. Decrypts it back
    4. Compares original vs decrypted byte-for-byte
    5. Verifies tamper detection (modifying one byte causes HMAC failure)
#>
param(
    [string]$DllPath = ""
)
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = (Resolve-Path "$PSScriptRoot\..\src\Overtake.SimHub.Plugin\bin\Release\Overtake.SimHub.Plugin.dll").Path
}

if (-not (Test-Path $DllPath)) {
    Write-Host "DLL not found: $DllPath" -ForegroundColor Red
    exit 1
}

Write-Host "=== OTK Security Validation ===" -ForegroundColor Cyan
Write-Host "DLL: $DllPath"
Write-Host ""

$asm = [System.Reflection.Assembly]::LoadFrom($DllPath)

$pass = 0
$fail = 0
$tempDir = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "otk_test_" + [guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

# Test 1: Round-trip integrity
Write-Host "[Test 1] Round-trip encrypt/decrypt..." -NoNewline
$testJson = '{"schema":"league-1.0","sessions":[{"type":"Race","track":"Monza"}],"participants":[{"tag":"TestDriver","team":"Mercedes"}]}'
$otkPath = [System.IO.Path]::Combine($tempDir, "test.otk")

$writerType = $asm.GetType("Overtake.SimHub.Plugin.Security.OtkWriter")
$bf = [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic
$writeMethod = $writerType.GetMethod("WriteOtk", $bf)
$readMethod = $writerType.GetMethod("ReadOtk", $bf)

$writeMethod.Invoke($null, @($testJson, $otkPath))

if (-not (Test-Path $otkPath)) {
    Write-Host " FAIL: .otk file not created" -ForegroundColor Red
    $fail++
} else {
    $decrypted = $readMethod.Invoke($null, @($otkPath))
    if ($decrypted -eq $testJson) {
        Write-Host " PASS" -ForegroundColor Green
        $pass++
    } else {
        Write-Host " FAIL: decrypted != original" -ForegroundColor Red
        Write-Host "  Expected: $testJson"
        Write-Host "  Got:      $decrypted"
        $fail++
    }
}

# Test 2: File starts with OTK1 magic
Write-Host "[Test 2] OTK1 magic header..." -NoNewline
$bytes = [System.IO.File]::ReadAllBytes($otkPath)
$magic = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4)
if ($magic -eq "OTK1") {
    Write-Host " PASS" -ForegroundColor Green
    $pass++
} else {
    Write-Host " FAIL: magic = '$magic'" -ForegroundColor Red
    $fail++
}

# Test 3: File is not plaintext-readable
Write-Host "[Test 3] Ciphertext is opaque (no JSON visible)..." -NoNewline
$rawText = [System.Text.Encoding]::UTF8.GetString($bytes)
if ($rawText.Contains("sessions") -or $rawText.Contains("participants")) {
    Write-Host " FAIL: plaintext JSON visible in .otk!" -ForegroundColor Red
    $fail++
} else {
    Write-Host " PASS" -ForegroundColor Green
    $pass++
}

# Test 4: Tamper detection (flip one ciphertext byte)
Write-Host "[Test 4] Tamper detection (flip 1 byte)..." -NoNewline
$tamperedPath = [System.IO.Path]::Combine($tempDir, "tampered.otk")
$tamperedBytes = [byte[]]$bytes.Clone()
$midpoint = [int]($tamperedBytes.Length / 2)
$tamperedBytes[$midpoint] = [byte]($tamperedBytes[$midpoint] -bxor 0xFF)
[System.IO.File]::WriteAllBytes($tamperedPath, $tamperedBytes)

$tamperDetected = $false
try {
    $readMethod.Invoke($null, @($tamperedPath))
} catch {
    if ($_.Exception.InnerException -and $_.Exception.InnerException.Message -match "HMAC|tamper|Padding") {
        $tamperDetected = $true
    } elseif ($_.Exception.Message -match "HMAC|tamper|Padding") {
        $tamperDetected = $true
    }
}
if ($tamperDetected) {
    Write-Host " PASS" -ForegroundColor Green
    $pass++
} else {
    Write-Host " FAIL: tampered file was accepted!" -ForegroundColor Red
    $fail++
}

# Test 5: Large payload round-trip
Write-Host "[Test 5] Large payload (100KB JSON)..." -NoNewline
$largeObj = @{
    schema = "league-1.0"
    sessions = @(@{ type = "Race"; track = "Monza"; laps = 1..200 | ForEach-Object { @{ lap = $_; time = "1:23.456"; sector1 = "0:28.123" } } })
    participants = 1..22 | ForEach-Object { @{ tag = "Driver_$_"; team = "Team_$_"; raceNumber = $_ } }
}
$largeJson = [string]($largeObj | ConvertTo-Json -Depth 10 -Compress)
$largePath = [System.IO.Path]::Combine($tempDir, "large.otk")
$writeMethod.Invoke($null, @($largeJson, $largePath))
$largeDecrypted = $readMethod.Invoke($null, @($largePath))
if ($largeDecrypted -eq $largeJson) {
    Write-Host " PASS ($([math]::Round($largeJson.Length/1024, 1)) KB)" -ForegroundColor Green
    $pass++
} else {
    Write-Host " FAIL: large payload mismatch" -ForegroundColor Red
    $fail++
}

# Test 6: Two encryptions of same data produce different ciphertexts (random IV)
Write-Host "[Test 6] Random IV (different ciphertexts)..." -NoNewline
$otk2Path = [System.IO.Path]::Combine($tempDir, "test2.otk")
$writeMethod.Invoke($null, @($testJson, $otk2Path))
$bytes1 = [System.IO.File]::ReadAllBytes($otkPath)
$bytes2 = [System.IO.File]::ReadAllBytes($otk2Path)
$identical = $true
if ($bytes1.Length -ne $bytes2.Length) { $identical = $false }
else {
    for ($i = 0; $i -lt $bytes1.Length; $i++) {
        if ($bytes1[$i] -ne $bytes2[$i]) { $identical = $false; break }
    }
}
if (-not $identical) {
    Write-Host " PASS" -ForegroundColor Green
    $pass++
} else {
    Write-Host " FAIL: identical ciphertexts!" -ForegroundColor Red
    $fail++
}

# Cleanup
Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Results: $pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($fail -gt 0) { exit 1 }
