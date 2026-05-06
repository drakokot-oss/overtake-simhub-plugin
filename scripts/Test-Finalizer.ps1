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

# Participants: 2 drivers (aligned with Test-SessionStore / ParticipantsData.Parse parity)
$pp = New-Object byte[] 1256
for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
$pp[0] = 2
$pp[1] = 0
$n0 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
[System.Array]::Copy($n0, 0, $pp, 8, $n0.Length)
$pp[4] = 0; $pp[6] = 44
$pp[41] = 1; $pp[44] = 1
$pp[58] = 0
$n1 = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
[System.Array]::Copy($n1, 0, $pp, 65, $n1.Length)
$pp[61] = 2; $pp[63] = 1
$pp[98] = 1; $pp[101] = 1
for ($c = 2; $c -lt 22; $c++) {
    $st = 1 + $c * 57
    $pp[$st + 3] = 255
    $pp[$st + 5] = 0
}
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

# Issue #1 diagnostics: networkId-keyed map and rn-key ambiguity list are exported
$diag = Get-DictValue $debug "diagnostics"
$lobbyInfo = Get-DictValue $diag "lobbyInfo"
Assert "Has lobbyInfo" ($lobbyInfo -ne $null)
Assert "Has bestKnownTagsByNet" ($lobbyInfo.ContainsKey("bestKnownTagsByNet"))
Assert "Has rnKeyAmbiguous" ($lobbyInfo.ContainsKey("rnKeyAmbiguous"))

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

# ---- Test 13: Online qualifying overflow phantom filter ----
# Helper to build an online quali store with N active drivers and (22-N) AI fillers.
# Returns the finalized session results count and dropped phantom tags.
function Test-OverflowFilter([int]$activeCount, [string]$caseName) {
    $st0 = [System.Activator]::CreateInstance($storeType)
    # Session: ShortQualifying (id=8), online
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 8; $sp[7] = 5
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp))))

    # Participants: $activeCount human drivers + (22-$activeCount) AI fillers
    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = [byte]$activeCount
    for ($c = 0; $c -lt 22; $c++) {
        $st = 1 + $c * 57
        if ($c -lt $activeCount) {
            $pp[$st + 0] = 0  # AiControlled=false
            $pp[$st + 40] = 1  # ShowOnlineNames=on
            $pp[$st + 41] = 1  # Platform = non-255
            $n = [System.Text.Encoding]::UTF8.GetBytes("Drv$c")
            [System.Array]::Copy($n, 0, $pp, ($st + 7), $n.Length)
        } else {
            $pp[$st + 0] = 1  # AiControlled=true
        }
        $pp[$st + 3] = [byte]($c % 10) # TeamId
        $pp[$st + 5] = [byte](10 + $c) # RaceNumber
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp))))

    # FC: 20 entries with Position > 0; first $activeCount have NumLaps=3, rest 0
    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 20
    for ($c = 0; $c -lt 20; $c++) {
        $off = 1 + $c * 46
        $fc[$off + 0] = [byte]($c + 1)
        if ($c -lt $activeCount) { $fc[$off + 1] = 3 } else { $fc[$off + 1] = 0 }
        $fc[$off + 2] = [byte]($c + 1)
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 8 $fc))))

    $res = $finalizeMethod.Invoke($null, @($st0))
    $ss = $res["sessions"]
    Assert "$caseName : session exists" ($ss.Count -ge 1)
    if ($ss.Count -lt 1) { return $null }
    $rsl = $ss[0]["results"]
    $cnt = if ($rsl -ne $null) { $rsl.Count } else { 0 }
    Assert "$caseName : result count == $activeCount (expected $activeCount, got $cnt)" ($cnt -eq $activeCount)

    $foundPhantom = $false
    if ($rsl -ne $null) {
        foreach ($r in $rsl) {
            $tg = Get-DictValue $r "tag"
            if ($tg -match '^Driver_\d+$') {
                # accept only if carIdx within active range (would mean a real player kept generic name)
                $cidx = Get-DictValue $r "carIdx"
                if ($cidx -ge $activeCount) {
                    $foundPhantom = $true
                    Write-Host "  Phantom kept: $tg (ci=$cidx, peak=$activeCount)" -ForegroundColor Red
                }
            }
        }
    }
    Assert "$caseName : no overflow phantoms" (-not $foundPhantom)
    return $cnt
}

Write-Host "=== Test 13a: Monaco-style (peak=18, 2 phantoms) ===" -ForegroundColor Cyan
[void](Test-OverflowFilter 18 "Monaco")

Write-Host "=== Test 13b: Miami-style (peak=19, 1 phantom) ===" -ForegroundColor Cyan
[void](Test-OverflowFilter 19 "Miami")

Write-Host "=== Test 13c: Baku-style (peak=16, 4 phantoms) ===" -ForegroundColor Cyan
[void](Test-OverflowFilter 16 "Baku")

Write-Host "=== Test 13d: Full grid (peak=20, no phantoms) ===" -ForegroundColor Cyan
[void](Test-OverflowFilter 20 "FullGrid")

# ---- Test 14: lobby-known players preserved on overflow / 0 laps (v1.1.30) ----
# Reproduces LasVegas UNAcapeleto: real player from lobby, ci in overflow range,
# 0 laps in the race FC (disconnected before lap 1). Must be PRESERVED.
# Also reproduces LasVegas Driver_18 quali: AI within active range, generic, 0 laps,
# NOT in lobby. Must be FILTERED.
function Test-LobbyKnownOverflow() {
    $st0 = [System.Activator]::CreateInstance($storeType)
    # Online race session
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 10; $sp[7] = 20  # Race-style id
    $sp[124] = 0; $sp[125] = 1            # NetworkGame=1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp))))

    # LobbyInfo packet (id 9): 18 known players including UNAcapeleto-equivalent.
    # Stride=42; baseOff = headerSize+1 = 30 (relative to packet); but as payload
    # we put numPlayers at index 0 and entries from index 1.
    $lobbyPlayers = 18
    $lobbyPayload = New-Object byte[] (1 + 22 * 42)
    $lobbyPayload[0] = $lobbyPlayers
    for ($lp = 0; $lp -lt $lobbyPlayers; $lp++) {
        $off = 1 + $lp * 42
        $lobbyPayload[$off + 0] = 0           # AI=false
        $lobbyPayload[$off + 1] = [byte]($lp % 10)   # TeamId
        $lobbyPayload[$off + 3] = 1           # Platform = Steam
        $nm = [System.Text.Encoding]::UTF8.GetBytes("Lobby$lp")
        [System.Array]::Copy($nm, 0, $lobbyPayload, ($off + 4), $nm.Length)
        $lobbyPayload[$off + 36] = [byte](20 + $lp)  # CarNumber (raceNumber)
        $lobbyPayload[$off + 38] = 1                  # ShowOnlineNames=on
    }
    # UNAcapeleto-style entry: rn=74, tid=3, lobby slot 17
    $offUna = 1 + 17 * 42
    $lobbyPayload[$offUna + 0] = 0
    $lobbyPayload[$offUna + 1] = 3
    $lobbyPayload[$offUna + 3] = 4
    $unaName = [System.Text.Encoding]::UTF8.GetBytes("UNAcapeleto")
    for ($zi = 0; $zi -lt 32; $zi++) { $lobbyPayload[$offUna + 4 + $zi] = 0 }
    [System.Array]::Copy($unaName, 0, $lobbyPayload, ($offUna + 4), $unaName.Length)
    $lobbyPayload[$offUna + 36] = 74
    $lobbyPayload[$offUna + 38] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 9 $lobbyPayload))))

    # Participants packet: 19 active (ci 0..18 humans, ci 19 = UNAcapeleto initially human)
    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 19
    for ($c = 0; $c -lt 22; $c++) {
        $st = 1 + $c * 57
        if ($c -lt 19) {
            $pp[$st + 0] = 0; $pp[$st + 40] = 1; $pp[$st + 41] = 1
            $pp[$st + 3] = [byte]($c % 10)
            $pp[$st + 5] = [byte](20 + $c)
        } elseif ($c -eq 19) {
            # Was originally UNAcapeleto but flipped to AI=true (disconnected -> AI took slot)
            $pp[$st + 0] = 1
            $pp[$st + 3] = 3
            $pp[$st + 5] = 74
        } else {
            $pp[$st + 0] = 1  # AI filler
            $pp[$st + 3] = [byte]($c % 10)
            $pp[$st + 5] = [byte](100 + $c)
        }
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp))))

    # FC: 20 entries; ci 0..18 finished with 5 laps; ci 19 (UNAcapeleto) 0 laps DNF
    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 20
    for ($c = 0; $c -lt 20; $c++) {
        $off = 1 + $c * 46
        $fc[$off + 0] = [byte]($c + 1)  # position
        if ($c -lt 19) { $fc[$off + 1] = 5 } else { $fc[$off + 1] = 0 }
        $fc[$off + 2] = [byte]($c + 1)  # carIdx is parser-computed; fixture path sets it
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 8 $fc))))

    $res = $finalizeMethod.Invoke($null, @($st0))
    $ss = $res["sessions"]
    Assert "v1.1.30: session exists" ($ss.Count -ge 1)
    if ($ss.Count -lt 1) { return }
    $rsl = $ss[0]["results"]
    $cnt = if ($rsl -ne $null) { $rsl.Count } else { 0 }

    $unaFound = $false
    if ($rsl -ne $null) {
        foreach ($r in $rsl) {
            $tg = Get-DictValue $r "tag"
            if ($tg -eq "UNAcapeleto") { $unaFound = $true }
        }
    }
    Assert "v1.1.30: UNAcapeleto preserved despite ci=19 overflow + 0 laps + AI flag" $unaFound
}

Write-Host "=== Test 14: lobby-known overflow players preserved (v1.1.30) ===" -ForegroundColor Cyan
[void](Test-LobbyKnownOverflow)

# ---- Summary ----
Write-Host ""
Write-Host "======================================" -ForegroundColor Yellow
$color = "Green"
if ($fail -gt 0) { $color = "Red" }
Write-Host "  PASS: $pass   FAIL: $fail" -ForegroundColor $color
Write-Host "======================================" -ForegroundColor Yellow
Write-Host ""
exit $fail
