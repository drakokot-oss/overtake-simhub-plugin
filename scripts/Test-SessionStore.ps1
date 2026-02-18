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
    $field = $obj.GetType().GetField($fieldName)
    if ($field -ne $null) { return $field.GetValue($obj) }
    $prop = $obj.GetType().GetProperty($fieldName)
    if ($prop -ne $null) { return $prop.GetValue($obj) }
    return $null
}

function Get-DictValue($dict, $key) {
    if ($dict -eq $null) { return $null }
    $outVal = $null
    $found = $dict.TryGetValue($key, [ref]$outVal)
    if ($found) { return $outVal }
    return $null
}

$storeType = $asm.GetType("Overtake.SimHub.Plugin.Store.SessionStore")
$parserType = $asm.GetType("Overtake.SimHub.Plugin.Parsers.PacketParser")

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

function GetDriver($sess, [string]$tag) {
    $drivers = Get-Field $sess "Drivers"
    return (Get-DictValue $drivers $tag)
}

function GetSession($store, [string]$sid) {
    $sessions = Get-Field $store "Sessions"
    return (Get-DictValue $sessions $sid)
}

$sid = "12345"
$ingestMethod = $storeType.GetMethod("Ingest")

function DoIngest($store, $parsed) {
    $ingestMethod.Invoke($store, @($parsed))
}

# ---- Test 1: SessionStore creation ----
Write-Host "=== Test 1: SessionStore creation ===" -ForegroundColor Cyan
$store = [System.Activator]::CreateInstance($storeType)
Assert "Store created" ($store -ne $null)
Assert "Connected false" ((Get-Field $store "Connected") -eq $false)
Assert "Sessions empty" ((Get-Field $store "Sessions").Count -eq 0)

# ---- Test 2: Ingest Session packet (ID 1) ----
Write-Host "=== Test 2: Ingest Session packet ===" -ForegroundColor Cyan
$sessionPayload = New-Object byte[] 700
$sessionPayload[0] = 1
$sessionPayload[1] = 30
$sessionPayload[2] = 22
$sessionPayload[6] = 10
$sessionPayload[7] = 5
$sessionPayload[124] = 0
$sessionPayload[125] = 1

$pkt = New-FakePacket 1 $sessionPayload
$parsed = Dispatch $pkt
Assert "Session parsed" ((Get-Field $parsed "Session") -ne $null)
DoIngest $store $parsed
Assert "Connected true" ((Get-Field $store "Connected") -eq $true)
Assert "1 session" ((Get-Field $store "Sessions").Count -eq 1)

$sess = GetSession $store $sid
Assert "SessionType 10" ((Get-Field $sess "SessionType") -eq 10)
Assert "TrackId 5" ((Get-Field $sess "TrackId") -eq 5)
Assert "Weather 1" ((Get-Field $sess "Weather") -eq 1)
Assert "NetworkGame 1" ((Get-Field $sess "NetworkGame") -eq 1)
Assert "WeatherTimeline has entry" ((Get-Field $sess "WeatherTimeline").Count -ge 1)

# ---- Test 3: Ingest Participants packet (ID 4) ----
Write-Host "=== Test 3: Ingest Participants ===" -ForegroundColor Cyan
$partPayload = New-Object byte[] 1256
$partPayload[0] = 2
$name0 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
[System.Array]::Copy($name0, 0, $partPayload, 8, $name0.Length)
$partPayload[1 + 3] = 1
$partPayload[1 + 5] = 44
$name1 = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
[System.Array]::Copy($name1, 0, $partPayload, 65, $name1.Length)
$partPayload[58 + 3] = 2
$partPayload[58 + 5] = 1

$pkt4 = New-FakePacket 4 $partPayload
$parsed4 = Dispatch $pkt4
Assert "Participants parsed" ((Get-Field $parsed4 "Participants") -ne $null)
DoIngest $store $parsed4
$sess = GetSession $store $sid
$tags = Get-Field $sess "TagsByCarIdx"
Assert "TagsByCarIdx has 2" ($tags.Count -eq 2)
Assert "Car 0 = Hamilton" ((Get-DictValue $tags 0) -eq "Hamilton")
Assert "Car 1 = Verstappen" ((Get-DictValue $tags 1) -eq "Verstappen")

# ---- Test 4: LapData - lap recording ----
Write-Host "=== Test 4: LapData lap recording ===" -ForegroundColor Cyan
$lapPayload = New-Object byte[] (22 * 57)
$lapPayload[33] = 1
$lapPayload[35] = 0
DoIngest $store (Dispatch (New-FakePacket 2 $lapPayload))

$hamDriver = GetDriver $sess "Hamilton"
Assert "Hamilton driver exists" ($hamDriver -ne $null)
Assert "Hamilton lastCurrentLapNum = 1" ((Get-Field $hamDriver "LastCurrentLapNum") -eq 1)

$lapPayload2 = New-Object byte[] (22 * 57)
$lapPayload2[33] = 2
[System.BitConverter]::GetBytes([uint32]85000).CopyTo($lapPayload2, 0)
[System.BitConverter]::GetBytes([uint16]28000).CopyTo($lapPayload2, 8)
$lapPayload2[10] = 0
[System.BitConverter]::GetBytes([uint16]27000).CopyTo($lapPayload2, 11)
$lapPayload2[13] = 0
DoIngest $store (Dispatch (New-FakePacket 2 $lapPayload2))

$hamDriver = GetDriver $sess "Hamilton"
$laps = Get-Field $hamDriver "Laps"
Assert "Hamilton has 1 lap" ($laps.Count -eq 1)
Assert "Lap 1 time 85000ms" ((Get-Field $laps[0] "LapTimeMs") -eq 85000)
Assert "Lap 1 sector1 28000" ((Get-Field $laps[0] "Sector1Ms") -eq 28000)
Assert "Lap 1 sector2 27000" ((Get-Field $laps[0] "Sector2Ms") -eq 27000)
Assert "Lap 1 sector3 30000" ((Get-Field $laps[0] "Sector3Ms") -eq 30000)
Assert "LastRecordedLapNumber = 1" ((Get-Field $hamDriver "LastRecordedLapNumber") -eq 1)

# ---- Test 5: Pit stop tracking ----
Write-Host "=== Test 5: Pit stop tracking ===" -ForegroundColor Cyan
$lapPayload3 = New-Object byte[] (22 * 57)
$lapPayload3[33] = 2
$lapPayload3[35] = 1
DoIngest $store (Dispatch (New-FakePacket 2 $lapPayload3))
$hamDriver = GetDriver $sess "Hamilton"
$pits = Get-Field $hamDriver "PitStops"
Assert "Pit stop recorded" ($pits.Count -eq 1)
Assert "PitStop numPitStops = 1" ((Get-Field $pits[0] "NumPitStops") -eq 1)

# ---- Test 6: Event PENA ----
Write-Host "=== Test 6: Event PENA ===" -ForegroundColor Cyan
$evtPayload = New-Object byte[] 20
[System.Text.Encoding]::ASCII.GetBytes("PENA").CopyTo($evtPayload, 0)
$evtPayload[4] = 2
$evtPayload[5] = 3
$evtPayload[6] = 0
$evtPayload[7] = 1
$evtPayload[8] = 5
$evtPayload[9] = 3
$evtPayload[10] = 0
DoIngest $store (Dispatch (New-FakePacket 3 $evtPayload))
$hamDriver = GetDriver $sess "Hamilton"
$penalties = Get-Field $hamDriver "PenaltySnapshots"
Assert "Hamilton has penalty" ($penalties.Count -ge 1)
$pen = $penalties[$penalties.Count - 1]
Assert "PenaltyType = 2" ((Get-Field $pen "PenaltyType") -eq 2)
Assert "InfringementType = 3" ((Get-Field $pen "InfringementType") -eq 3)

# ---- Test 7: Event COLL ----
Write-Host "=== Test 7: Event COLL ===" -ForegroundColor Cyan
$collPayload = New-Object byte[] 20
[System.Text.Encoding]::ASCII.GetBytes("COLL").CopyTo($collPayload, 0)
$collPayload[4] = 0
$collPayload[5] = 1
DoIngest $store (Dispatch (New-FakePacket 3 $collPayload))
$hamDriver = GetDriver $sess "Hamilton"
$verDriver = GetDriver $sess "Verstappen"
Assert "Verstappen driver exists" ($verDriver -ne $null)
$hamPen = Get-Field $hamDriver "PenaltySnapshots"
$hamCollFound = $false
foreach ($p in $hamPen) { if ((Get-Field $p "EventCode") -eq "COLL") { $hamCollFound = $true } }
Assert "Hamilton COLL recorded" $hamCollFound
if ($verDriver -ne $null) {
    $verPen = Get-Field $verDriver "PenaltySnapshots"
    $verCollFound = $false
    foreach ($p in $verPen) { if ((Get-Field $p "EventCode") -eq "COLL") { $verCollFound = $true } }
    Assert "Verstappen COLL recorded" $verCollFound
}

# ---- Test 8: Event SCAR ----
Write-Host "=== Test 8: Event SCAR ===" -ForegroundColor Cyan
$scarPayload = New-Object byte[] 20
[System.Text.Encoding]::ASCII.GetBytes("SCAR").CopyTo($scarPayload, 0)
$scarPayload[4] = 1
$scarPayload[5] = 0
DoIngest $store (Dispatch (New-FakePacket 3 $scarPayload))
$sess = GetSession $store $sid
Assert "SC deployment counted" ((Get-Field $sess "NumSafetyCarDeployments") -eq 1)

$scarPayload2 = New-Object byte[] 20
[System.Text.Encoding]::ASCII.GetBytes("SCAR").CopyTo($scarPayload2, 0)
$scarPayload2[4] = 3
$scarPayload2[5] = 3
DoIngest $store (Dispatch (New-FakePacket 3 $scarPayload2))
Assert "Formation SCAR not counted" ((Get-Field $sess "NumSafetyCarDeployments") -eq 1)

# ---- Test 9: CarDamage ----
Write-Host "=== Test 9: CarDamage ===" -ForegroundColor Cyan
$dmgPayload = New-Object byte[] (22 * 46)
[System.BitConverter]::GetBytes([float]10.5).CopyTo($dmgPayload, 0)
[System.BitConverter]::GetBytes([float]11.0).CopyTo($dmgPayload, 4)
[System.BitConverter]::GetBytes([float]9.5).CopyTo($dmgPayload, 8)
[System.BitConverter]::GetBytes([float]10.0).CopyTo($dmgPayload, 12)
$dmgPayload[28] = 5
$dmgPayload[29] = 3
$dmgPayload[30] = 0
DoIngest $store (Dispatch (New-FakePacket 10 $dmgPayload))
$hamDriver = GetDriver $sess "Hamilton"
$tw = Get-Field $hamDriver "LatestTyreWear"
Assert "TyreWear stored" ($tw -ne $null)
Assert "TyreWear RL ~10.5" ([Math]::Abs((Get-Field $tw "RL") - 10.5) -lt 0.1)
$dmg = Get-Field $hamDriver "LatestDamage"
Assert "Damage stored" ($dmg -ne $null)
Assert "WingFL = 5" ((Get-Field $dmg "WingFrontLeft") -eq 5)

# ---- Test 10: TyreWear + Damage snapshots on lap ----
Write-Host "=== Test 10: TyreWear + Damage per lap ===" -ForegroundColor Cyan
$lapPayload4 = New-Object byte[] (22 * 57)
$lapPayload4[33] = 3
[System.BitConverter]::GetBytes([uint32]86000).CopyTo($lapPayload4, 0)
[System.BitConverter]::GetBytes([uint16]29000).CopyTo($lapPayload4, 8)
$lapPayload4[10] = 0
[System.BitConverter]::GetBytes([uint16]28000).CopyTo($lapPayload4, 11)
$lapPayload4[13] = 0
DoIngest $store (Dispatch (New-FakePacket 2 $lapPayload4))
$hamDriver = GetDriver $sess "Hamilton"
$twPerLap = Get-Field $hamDriver "TyreWearPerLap"
Assert "TyreWear snapshot created" ($twPerLap.Count -ge 1)
$lastTw = $twPerLap[$twPerLap.Count - 1]
Assert "TW snapshot lap = 2" ((Get-Field $lastTw "LapNumber") -eq 2)
$dmgPerLap = Get-Field $hamDriver "DamagePerLap"
Assert "Damage snapshot created" ($dmgPerLap.Count -ge 1)
$lastDmg = $dmgPerLap[$dmgPerLap.Count - 1]
Assert "Dmg snapshot lap = 2" ((Get-Field $lastDmg "LapNumber") -eq 2)
Assert "Dmg WingFL = 5" ((Get-Field $lastDmg "WingFL") -eq 5)

# ---- Test 11: FinalClassification ----
Write-Host "=== Test 11: FinalClassification ===" -ForegroundColor Cyan
$fcPayload = New-Object byte[] (1 + 22 * 46)
$fcPayload[0] = 2
$fcPayload[1] = 1
$fcPayload[2] = 5
DoIngest $store (Dispatch (New-FakePacket 8 $fcPayload))
$sess = GetSession $store $sid
Assert "FinalClassification stored" ((Get-Field $sess "FinalClassification") -ne $null)
Assert "SessionEndedAtMs set" ((Get-Field $sess "SessionEndedAtMs") -ne $null)

# ---- Test 12: SSTA offline reset ----
Write-Host "=== Test 12: SSTA offline reset ===" -ForegroundColor Cyan
$storeOff = [System.Activator]::CreateInstance($storeType)
$sessPayloadOff = New-Object byte[] 700
$sessPayloadOff[6] = 10
$sessPayloadOff[125] = 0
DoIngest $storeOff (Dispatch (New-FakePacket 1 $sessPayloadOff))
DoIngest $storeOff (Dispatch (New-FakePacket 4 $partPayload))

$lapOff = New-Object byte[] (22 * 57)
$lapOff[33] = 1
DoIngest $storeOff (Dispatch (New-FakePacket 2 $lapOff))
$lapOff2 = New-Object byte[] (22 * 57)
$lapOff2[33] = 2
[System.BitConverter]::GetBytes([uint32]80000).CopyTo($lapOff2, 0)
DoIngest $storeOff (Dispatch (New-FakePacket 2 $lapOff2))

$sessOff = GetSession $storeOff $sid
$hamOff = GetDriver $sessOff "Hamilton"
$lapsBeforeReset = (Get-Field $hamOff "Laps").Count
Assert "Has lap before reset" ($lapsBeforeReset -ge 1)

$sstaPayload = New-Object byte[] 20
[System.Text.Encoding]::ASCII.GetBytes("SSTA").CopyTo($sstaPayload, 0)
DoIngest $storeOff (Dispatch (New-FakePacket 3 $sstaPayload))
$hamOff = GetDriver $sessOff "Hamilton"
Assert "Offline SSTA resets laps" ((Get-Field $hamOff "Laps").Count -eq 0)
Assert "SSTA resets LastRecordedLapNumber" ((Get-Field $hamOff "LastRecordedLapNumber") -eq 0)

# ---- Test 13: Cross-session name resolution ----
Write-Host "=== Test 13: Cross-session name resolution ===" -ForegroundColor Cyan
$storeXsess = [System.Activator]::CreateInstance($storeType)
DoIngest $storeXsess (Dispatch (New-FakePacket 1 $sessionPayload))
DoIngest $storeXsess (Dispatch (New-FakePacket 4 $partPayload))
$sessX = GetSession $storeXsess $sid
Assert "Session 1: Hamilton" ((Get-DictValue (Get-Field $sessX "TagsByCarIdx") 0) -eq "Hamilton")

$partGeneric = New-Object byte[] 1256
$partGeneric[0] = 1
$genName = [System.Text.Encoding]::UTF8.GetBytes("Driver_0")
[System.Array]::Copy($genName, 0, $partGeneric, 8, $genName.Length)
$partGeneric[1 + 3] = 1
$partGeneric[1 + 5] = 44

$hdr2 = New-Object byte[] 29
[System.BitConverter]::GetBytes([uint16]2025).CopyTo($hdr2, 0)
$hdr2[2] = 25; $hdr2[3] = 1; $hdr2[4] = 0; $hdr2[5] = 1; $hdr2[6] = 4
[System.BitConverter]::GetBytes([uint64]99999).CopyTo($hdr2, 7)
[System.BitConverter]::GetBytes([float]200.0).CopyTo($hdr2, 15)
[System.BitConverter]::GetBytes([uint32]2).CopyTo($hdr2, 19)
[System.BitConverter]::GetBytes([uint32]2).CopyTo($hdr2, 23)
$hdr2[27] = 0; $hdr2[28] = 255
$full2 = New-Object byte[] ($hdr2.Length + $partGeneric.Length)
[System.Array]::Copy($hdr2, 0, $full2, 0, $hdr2.Length)
[System.Array]::Copy($partGeneric, 0, $full2, $hdr2.Length, $partGeneric.Length)
DoIngest $storeXsess (Dispatch $full2)
$sessX2 = GetSession $storeXsess "99999"
Assert "Generic resolved to Hamilton" ((Get-DictValue (Get-Field $sessX2 "TagsByCarIdx") 0) -eq "Hamilton")

# ---- Test 14: Diagnostics ----
Write-Host "=== Test 14: Diagnostics ===" -ForegroundColor Cyan
Assert "DiagLdLapRecorded > 0" ((Get-Field $store "DiagLdLapRecorded") -gt 0)
Assert "PacketCounts has entries" ((Get-Field $store "PacketCounts").Count -gt 0)
Assert "SessionUid set" ((Get-Field $store "SessionUid") -eq 12345)

# ---- Test 15: Warnings tracking ----
Write-Host "=== Test 15: Warnings tracking ===" -ForegroundColor Cyan
$warnPayload = New-Object byte[] (22 * 57)
$warnPayload[33] = 3
$warnPayload[39] = 2
$warnPayload[40] = 1
DoIngest $store (Dispatch (New-FakePacket 2 $warnPayload))
$hamDriver = GetDriver (GetSession $store $sid) "Hamilton"
Assert "TotalWarnings = 2" ((Get-Field $hamDriver "LastTotalWarnings") -eq 2)
Assert "CornerCuttingWarnings = 1" ((Get-Field $hamDriver "LastCornerCuttingWarnings") -eq 1)

# ---- Summary ----
Write-Host ""
Write-Host "======================================" -ForegroundColor Yellow
$color = "Green"
if ($fail -gt 0) { $color = "Red" }
Write-Host "  PASS: $pass   FAIL: $fail" -ForegroundColor $color
Write-Host "======================================" -ForegroundColor Yellow
Write-Host ""
exit $fail
