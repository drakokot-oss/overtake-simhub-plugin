param(
    [string]$DllPath = "$PSScriptRoot\..\src\Overtake.SimHub.Plugin\bin\Release\Overtake.SimHub.Plugin.dll"
)
$ErrorActionPreference = "Stop"
$pass = 0; $fail = 0

$asm = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $DllPath))

function Assert($name, $condition) {
    if ($condition) { $script:pass++ }
    else { $script:fail++; Write-Host "  FAIL: $name" -ForegroundColor Red }
}

function Get-Field($obj, [string]$fieldName) {
    if ($obj -eq $null) { return $null }
    $f = $obj.GetType().GetField($fieldName)
    if ($f -ne $null) { return $f.GetValue($obj) }
    $p = $obj.GetType().GetProperty($fieldName)
    if ($p -ne $null) { return $p.GetValue($obj) }
    return $null
}

function Get-DictValue($dict, $key) {
    if ($dict -eq $null) { return $null }
    $out = $null
    $found = $dict.TryGetValue($key, [ref]$out)
    if ($found) { return $out }
    return $null
}

$storeType = $asm.GetType("Overtake.SimHub.Plugin.Store.SessionStore")
$parserType = $asm.GetType("Overtake.SimHub.Plugin.Parsers.PacketParser")
$finalizerType = $asm.GetType("Overtake.SimHub.Plugin.Finalizer.LeagueFinalizer")
$lookupsType = $asm.GetType("Overtake.SimHub.Plugin.Finalizer.Lookups")

function New-FakePacket([int]$packetId, [byte[]]$payload, [uint64]$sessionUid = 12345) {
    $header = New-Object byte[] 29
    [System.BitConverter]::GetBytes([uint16]2025).CopyTo($header, 0)
    $header[2] = 25; $header[3] = 1; $header[4] = 0; $header[5] = 1
    $header[6] = [byte]$packetId
    [System.BitConverter]::GetBytes($sessionUid).CopyTo($header, 7)
    [System.BitConverter]::GetBytes([float]100.0).CopyTo($header, 15)
    [System.BitConverter]::GetBytes([uint32]1).CopyTo($header, 19)
    [System.BitConverter]::GetBytes([uint32]1).CopyTo($header, 23)
    $header[27] = 0; $header[28] = 255
    $full = New-Object byte[] ($header.Length + $payload.Length)
    [System.Array]::Copy($header, 0, $full, 0, $header.Length)
    [System.Array]::Copy($payload, 0, $full, $header.Length, $payload.Length)
    return ,$full
}

function Dispatch([byte[]]$pkt) {
    $method = $parserType.GetMethod("Dispatch")
    return $method.Invoke($null, [object[]]@(,[byte[]]$pkt))
}

$ingestMethod = $storeType.GetMethod("Ingest")
$finalizeMethod = $finalizerType.GetMethod("Finalize")

# ---- Build a realistic store ----
Write-Host "=== Building test store ===" -ForegroundColor Cyan
$store = [System.Activator]::CreateInstance($storeType)

# Session (Race, Monaco)
$sp = New-Object byte[] 700
$sp[0] = 1; $sp[1] = 28; $sp[2] = 20; $sp[6] = 10; $sp[7] = 5
$sp[124] = 0; $sp[125] = 1
$ingestMethod.Invoke($store, @((Dispatch (New-FakePacket 1 $sp))))

# Participants: 2 drivers
$pp = New-Object byte[] 1256
$pp[0] = 2
$n0 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
[System.Array]::Copy($n0, 0, $pp, 8, $n0.Length)
$pp[1+3] = 0; $pp[1+5] = 44
$n1 = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
[System.Array]::Copy($n1, 0, $pp, 65, $n1.Length)
$pp[58+3] = 2; $pp[58+5] = 1
$ingestMethod.Invoke($store, @((Dispatch (New-FakePacket 4 $pp))))

# LapData: simulate 3 laps for Hamilton
$ld1 = New-Object byte[] (22 * 57)
$ld1[33] = 1
$ingestMethod.Invoke($store, @((Dispatch (New-FakePacket 2 $ld1))))

$ld2 = New-Object byte[] (22 * 57)
$ld2[33] = 2
[System.BitConverter]::GetBytes([uint32]85000).CopyTo($ld2, 0)
[System.BitConverter]::GetBytes([uint16]28000).CopyTo($ld2, 8)
[System.BitConverter]::GetBytes([uint16]27000).CopyTo($ld2, 11)
$ingestMethod.Invoke($store, @((Dispatch (New-FakePacket 2 $ld2))))

$ld3 = New-Object byte[] (22 * 57)
$ld3[33] = 3
[System.BitConverter]::GetBytes([uint32]83000).CopyTo($ld3, 0)
[System.BitConverter]::GetBytes([uint16]27000).CopyTo($ld3, 8)
[System.BitConverter]::GetBytes([uint16]26000).CopyTo($ld3, 11)
$ingestMethod.Invoke($store, @((Dispatch (New-FakePacket 2 $ld3))))

# FinalClassification
$fc = New-Object byte[] (1 + 22 * 46)
$fc[0] = 2
$fc[1] = 1; $fc[2] = 3; $fc[3] = 2; $fc[5] = 0; $fc[6] = 3
$off1 = 1 + 46
$fc[$off1] = 2; $fc[$off1+1] = 3; $fc[$off1+2] = 1; $fc[$off1+5] = 0; $fc[$off1+6] = 3
$ingestMethod.Invoke($store, @((Dispatch (New-FakePacket 8 $fc))))

# ---- Test 1: Finalize produces valid structure ----
Write-Host "=== Test 1: Finalize structure ===" -ForegroundColor Cyan
$result = $finalizeMethod.Invoke($null, @($store))
Assert "Result not null" ($result -ne $null)
Assert "Has schemaVersion" ((Get-DictValue $result "schemaVersion") -eq "league-1.0")
Assert "Has game" ((Get-DictValue $result "game") -eq "F1_25")
Assert "Has capture" ((Get-DictValue $result "capture") -ne $null)
Assert "Has participants" ((Get-DictValue $result "participants") -ne $null)
Assert "Has sessions" ((Get-DictValue $result "sessions") -ne $null)

$sessions = $result["sessions"]
Assert "Sessions not null" ($sessions -ne $null)
Assert "1 session" ($sessions.Count -eq 1)

# ---- Test 2: Session content ----
Write-Host "=== Test 2: Session content ===" -ForegroundColor Cyan
$s = $sessions[0]
$st = Get-DictValue $s "sessionType"
Assert "SessionType is dict" ($st -ne $null)
Assert "SessionType name = Race" ((Get-DictValue $st "name") -eq "Race")
Assert "SessionType id = 10" ((Get-DictValue $st "id") -eq 10)

$track = Get-DictValue $s "track"
Assert "Track name = Monaco" ((Get-DictValue $track "name") -eq "Monaco")

$weather = Get-DictValue $s "weather"
Assert "Weather name = LightCloud" ((Get-DictValue $weather "name") -eq "LightCloud")

# ---- Test 3: Results ----
Write-Host "=== Test 3: Results ===" -ForegroundColor Cyan
$results = Get-DictValue $s "results"
Assert "Has results" ($results -ne $null -and $results.Count -ge 1)
$r1 = $results[0]
Assert "Result 1 position = 1" ((Get-DictValue $r1 "position") -eq 1)
Assert "Result 1 tag = Hamilton" ((Get-DictValue $r1 "tag") -eq "Hamilton")
Assert "Result 1 teamName" ((Get-DictValue $r1 "teamName") -eq "Mercedes-AMG Petronas")

# ---- Test 4: Drivers payload ----
Write-Host "=== Test 4: Drivers payload ===" -ForegroundColor Cyan
$drivers = Get-DictValue $s "drivers"
Assert "Drivers dict not null" ($drivers -ne $null)
$hamD = Get-DictValue $drivers "Hamilton"
Assert "Hamilton driver exists" ($hamD -ne $null)
$hamLaps = Get-DictValue $hamD "laps"
Assert "Hamilton has laps" ($hamLaps -ne $null -and $hamLaps.Count -ge 1)
$lap1 = $hamLaps[0]
Assert "Lap 1 has lapNumber" ((Get-DictValue $lap1 "lapNumber") -eq 1)
Assert "Lap 1 has lapTimeMs" ((Get-DictValue $lap1 "lapTimeMs") -eq 85000)
Assert "Lap 1 has lapTime string" ((Get-DictValue $lap1 "lapTime") -ne $null)

# ---- Test 5: Awards ----
Write-Host "=== Test 5: Awards ===" -ForegroundColor Cyan
$awards = Get-DictValue $s "awards"
Assert "Awards not null" ($awards -ne $null)
Assert "Has fastestLap key" ($awards.ContainsKey("fastestLap"))
Assert "Has mostPositionsGained key" ($awards.ContainsKey("mostPositionsGained"))
Assert "Has mostConsistent key" ($awards.ContainsKey("mostConsistent"))

# ---- Test 6: Safety Car section ----
Write-Host "=== Test 6: Safety car ===" -ForegroundColor Cyan
$sc = Get-DictValue $s "safetyCar"
Assert "SafetyCar not null" ($sc -ne $null)
Assert "Has status" ((Get-DictValue $sc "status") -ne $null)
Assert "Has fullDeploys" ($sc.ContainsKey("fullDeploys"))

# ---- Test 7: Lookups sanity ----
Write-Host "=== Test 7: Lookups ===" -ForegroundColor Cyan
$teamsField = $lookupsType.GetField("Teams")
$teams = $teamsField.GetValue($null)
Assert "Teams has Mercedes" ((Get-DictValue $teams 0) -eq "Mercedes-AMG Petronas")
Assert "Teams has Ferrari" ((Get-DictValue $teams 1) -eq "Scuderia Ferrari HP")
Assert "Teams has Red Bull" ((Get-DictValue $teams 2) -eq "Red Bull Racing")

$tracksField = $lookupsType.GetField("Tracks")
$tracks = $tracksField.GetValue($null)
Assert "Tracks has Monaco" ((Get-DictValue $tracks 5) -eq "Monaco")
Assert "Tracks has Spa" ((Get-DictValue $tracks 10) -eq "Spa")

# ---- Test 8: Capture metadata ----
Write-Host "=== Test 8: Capture metadata ===" -ForegroundColor Cyan
$capture = Get-DictValue $result "capture"
Assert "Capture sessionUID" ((Get-DictValue $capture "sessionUID") -eq "12345")
Assert "Capture startedAtMs > 0" ((Get-DictValue $capture "startedAtMs") -gt 0)
Assert "Capture has sessionTypes" ((Get-DictValue $capture "sessionTypesInCapture") -ne $null)

# ---- Test 9: Participants ----
Write-Host "=== Test 9: Participants ===" -ForegroundColor Cyan
$parts = Get-DictValue $result "participants"
Assert "Participants has Hamilton" ($parts -contains "Hamilton")
Assert "Participants has Verstappen" ($parts -contains "Verstappen")

# ---- Test 10: Debug section ----
Write-Host "=== Test 10: Debug section ===" -ForegroundColor Cyan
$debug = Get-DictValue $result "_debug"
Assert "Debug not null" ($debug -ne $null)
Assert "Has packetIdCounts" ((Get-DictValue $debug "packetIdCounts") -ne $null)
Assert "Has diagnostics" ((Get-DictValue $debug "diagnostics") -ne $null)

# ---- Test 11: JSON serialization ----
Write-Host "=== Test 11: JSON serialization ===" -ForegroundColor Cyan
[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$serType = [System.Web.Script.Serialization.JavaScriptSerializer]
$ser = New-Object $serType
$ser.MaxJsonLength = [int]::MaxValue
$json = $ser.Serialize($result)
Assert "JSON not empty" ($json.Length -gt 100)
Assert "JSON contains league-1.0" ($json.Contains("league-1.0"))
Assert "JSON contains Hamilton" ($json.Contains("Hamilton"))
Assert "JSON contains Monaco" ($json.Contains("Monaco"))
Assert "JSON contains Race" ($json.Contains("Race"))
Write-Host "  JSON size: $($json.Length) chars" -ForegroundColor Gray

# ---- Test 12: Qualifying fallback ----
Write-Host "=== Test 12: Qualifying fallback ===" -ForegroundColor Cyan
$storeQ = [System.Activator]::CreateInstance($storeType)
$spQ = New-Object byte[] 700
$spQ[6] = 5
$ingestMethod.Invoke($storeQ, @((Dispatch (New-FakePacket 1 $spQ))))
$ingestMethod.Invoke($storeQ, @((Dispatch (New-FakePacket 4 $pp))))

$ldQ = New-Object byte[] (22 * 57)
$ldQ[33] = 1
$ingestMethod.Invoke($storeQ, @((Dispatch (New-FakePacket 2 $ldQ))))
$ldQ2 = New-Object byte[] (22 * 57)
$ldQ2[33] = 2
[System.BitConverter]::GetBytes([uint32]90000).CopyTo($ldQ2, 0)
$ingestMethod.Invoke($storeQ, @((Dispatch (New-FakePacket 2 $ldQ2))))

$resultQ = $finalizeMethod.Invoke($null, @($storeQ))
$sessionsQ = $resultQ["sessions"]
Assert "Quali session exists" ($sessionsQ.Count -ge 1)
if ($sessionsQ.Count -ge 1) {
    $sQ = $sessionsQ[0]
    $resultsQ = $sQ["results"]
    Assert "Quali has fallback results" ($resultsQ -ne $null -and $resultsQ.Count -ge 1)
    if ($resultsQ -ne $null -and $resultsQ.Count -ge 1) {
        $r1Q = $resultsQ[0]
        Assert "Quali result has position" ($r1Q["position"] -ge 1)
    }
}

# ---- Summary ----
Write-Host ""
Write-Host "======================================" -ForegroundColor Yellow
$color = "Green"
if ($fail -gt 0) { $color = "Red" }
Write-Host "  PASS: $pass   FAIL: $fail" -ForegroundColor $color
Write-Host "======================================" -ForegroundColor Yellow
Write-Host ""
exit $fail
