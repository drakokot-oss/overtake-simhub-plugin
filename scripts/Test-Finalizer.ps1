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
    if (-not $found) { return $null }
    # PowerShell function returns auto-enumerate IEnumerable. For collections
    # (List<object>, arrays, etc.) this unrolls a 1-item list into the single
    # contained item, so callers see a Dictionary instead of a List of 1, and
    # `.Count` then returns the dict's key count. Wrapping with the unary
    # comma operator forces a 1-element array around the value, preventing
    # the pipeline from unrolling. We only do this for IList because Dictionary
    # does not implement it and scalars must remain scalars (so `-eq` keeps
    # its usual semantics).
    if ($out -is [System.Collections.IList]) { return ,$out }
    return $out
}

$storeType = $asm.GetType("Overtake.SimHub.Plugin.Store.SessionStore")
$parserType = $asm.GetType("Overtake.SimHub.Plugin.Parsers.PacketParser")
$finalizerType = $asm.GetType("Overtake.SimHub.Plugin.Finalizer.LeagueFinalizer")
$lookupsType = $asm.GetType("Overtake.SimHub.Plugin.Finalizer.Lookups")

function New-FakePacket([int]$packetId, [byte[]]$payload, [uint64]$sessionUid = 12345, [uint16]$packetFormat = 2025) {
    $header = New-Object byte[] 29
    [System.BitConverter]::GetBytes([uint16]$packetFormat).CopyTo($header, 0)
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
$sp[0] = 1; $sp[1] = 28; $sp[2] = 20; $sp[6] = 15; $sp[7] = 5
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
Assert "Has schemaVersion" ((Get-DictValue $result "schemaVersion") -eq "league-1.1")
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
Assert "SessionType id = 15" ((Get-DictValue $st "id") -eq 15)

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
Assert "JSON contains league-1.1" ($json.Contains("league-1.1"))
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
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 20  # Race-style id
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

# ---- Test 15: auto-rotation guard on track change (v1.1.31) ----
# Reproduces the Monaco_20260507 issue: a single capture received Baku weekend
# (SS, OSQ, Race with FC) followed by Monaco (Quali, Race). Without rotation, both
# tracks accumulate in the same store. With Camada 1, the moment a Session packet
# announces a different trackId AND we already have a closed Race, the store sets
# AutoRotateRequested=true and refuses to ingest the new track. Camada 5 catches
# residual multi-track stores at Finalize and drops everything but the latest.
function Test-AutoRotateOnTrackChange() {
    $st0 = [System.Activator]::CreateInstance($storeType)

    # --- Baku Race (track 20), with FC so it counts as a "closed terminal session" ---
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 20  # sessionType=15 (Race, F1 25 spec), trackId=20 (Baku)
    $sp[124] = 0; $sp[125] = 1            # NetworkGame=1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp -sessionUid ([uint64]7000)))))

    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 1
    $pp[1] = 0  # AI=false
    $pp[5] = 99  # raceNumber
    $pp[3] = 1   # teamId
    $pp[40] = 1; $pp[41] = 1
    $n = [System.Text.Encoding]::UTF8.GetBytes("BakuDriver")
    [System.Array]::Copy($n, 0, $pp, 8, $n.Length)
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp -sessionUid ([uint64]7000)))))

    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 1
    $fc[1] = 1; $fc[2] = 5; $fc[3] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 8 $fc -sessionUid ([uint64]7000)))))

    Assert "v1.1.31: HasClosedTerminalSession after Baku FC" `
        ($storeType.GetMethod("HasClosedTerminalSession").Invoke($st0, $null))
    Assert "v1.1.31: AutoRotate not yet requested" `
        (-not [bool]$storeType.GetProperty("AutoRotateRequested").GetValue($st0))

    # --- Monaco Quali first packet (track 5) - must trigger auto-rotate ---
    $spM = New-Object byte[] 700
    $spM[0] = 1; $spM[6] = 8; $spM[7] = 5   # sessionType=8 (ShortQualifying), trackId=5 (Monaco)
    $spM[124] = 0; $spM[125] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $spM -sessionUid ([uint64]9000)))))

    Assert "v1.1.31: AutoRotate REQUESTED after Monaco track change" `
        ([bool]$storeType.GetProperty("AutoRotateRequested").GetValue($st0))

    # The Baku store should still hold ONE session (Baku); Monaco's first packet was rejected
    $sessDict = Get-Field $st0 "Sessions"
    Assert "v1.1.31: Monaco packet rejected - store still has 1 session (Baku)" `
        ($sessDict.Count -eq 1)
    foreach ($k in $sessDict.Keys) {
        $tid = Get-Field $sessDict[$k] "TrackId"
        Assert "v1.1.31: only Baku trackId=20 in store" ($tid -eq 20)
    }

    # Subsequent Monaco packets (Participants, LapData) sent BEFORE OvertakePlugin handles
    # the rotation must also be dropped - otherwise they'd corrupt the old (Baku) store.
    $ppM = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $ppM.Length; $zi++) { $ppM[$zi] = 0 }
    $ppM[0] = 1; $ppM[1] = 0; $ppM[3] = 2; $ppM[5] = 44; $ppM[40] = 1; $ppM[41] = 1
    $nm = [System.Text.Encoding]::UTF8.GetBytes("MonacoLeaker")
    [System.Array]::Copy($nm, 0, $ppM, 8, $nm.Length)
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $ppM -sessionUid ([uint64]9000)))))

    $ldM = New-Object byte[] (22 * 57)
    $ldM[33] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ldM -sessionUid ([uint64]9000)))))

    $sessDict = Get-Field $st0 "Sessions"
    Assert "v1.1.31: Subsequent Monaco packets also dropped - still 1 session" `
        ($sessDict.Count -eq 1)
    foreach ($k in $sessDict.Keys) {
        $sess = $sessDict[$k]
        $drvs = Get-Field $sess "Drivers"
        # Baku had 1 driver (BakuDriver). MonacoLeaker must NOT appear.
        $hasLeaker = $drvs.ContainsKey("MonacoLeaker")
        Assert "v1.1.31: MonacoLeaker NOT leaked into Baku store" (-not $hasLeaker)
    }

    # Simulate the OvertakePlugin reaction: clear request, BeginNewCapture, then ingest Monaco
    $storeType.GetMethod("ClearAutoRotateRequest").Invoke($st0, $null)
    $storeType.GetMethod("BeginNewCapture").Invoke($st0, $null)
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $spM -sessionUid ([uint64]9000)))))

    $sessDict = Get-Field $st0 "Sessions"
    Assert "v1.1.31: After rotate+ingest, store has fresh Monaco session" `
        ($sessDict.Count -eq 1)
    foreach ($k in $sessDict.Keys) {
        $tid = Get-Field $sessDict[$k] "TrackId"
        Assert "v1.1.31: store now contains only Monaco trackId=5" ($tid -eq 5)
    }
}

Write-Host "=== Test 15: auto-rotate on track change (v1.1.31) ===" -ForegroundColor Cyan
[void](Test-AutoRotateOnTrackChange)

# ---- Test 16: defense-in-depth multi-track guard at Finalize (Camada 5) ----
# When auto-rotation didn't happen (e.g. user disabled AutoExport), the LeagueFinalizer
# drops sessions from all tracks except the most recently active one and emits a
# [POST-HOC] note in _debug.notes.
function Test-MultiTrackGuard() {
    $st0 = [System.Activator]::CreateInstance($storeType)

    # Force two sessions with different trackIds by directly populating Sessions via packets
    # but skipping the auto-rotate trigger (no FC on the first session).
    $sp1 = New-Object byte[] 700
    $sp1[0] = 1; $sp1[6] = 9; $sp1[7] = 20  # OneShotQualifying @ Baku (no FC -> not "closed")
    $sp1[124] = 0; $sp1[125] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp1 -sessionUid ([uint64]4000)))))

    $sp2 = New-Object byte[] 700
    $sp2[0] = 1; $sp2[6] = 10; $sp2[7] = 5  # Race @ Monaco (latest)
    $sp2[124] = 0; $sp2[125] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp2 -sessionUid ([uint64]5000)))))

    # Without HasClosedTerminalSession=true, no auto-rotate fires; both sessions stay
    $sessDict = Get-Field $st0 "Sessions"
    Assert "v1.1.31: pre-finalize, store has 2 sessions across 2 tracks" ($sessDict.Count -eq 2)

    # Camada 5 in action: Finalize must keep Monaco only and emit a note
    $res = $finalizeMethod.Invoke($null, @($st0))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.31: Finalize output has only 1 session" ($sessions.Count -eq 1)
    if ($sessions.Count -ge 1) {
        $tk = Get-DictValue $sessions[0] "track"
        Assert "v1.1.31: kept session is Monaco (latest)" ((Get-DictValue $tk "name") -eq "Monaco")
    }
    $debug = Get-DictValue $res "_debug"
    $notes = Get-DictValue $debug "notes"
    $hasNote = $false
    if ($notes -ne $null) {
        foreach ($note in $notes) {
            if ($note -match "Multi-track capture detected") { $hasNote = $true }
        }
    }
    Assert "v1.1.31: [POST-HOC] multi-track note emitted in _debug.notes" $hasNote
}

Write-Host "=== Test 16: defense-in-depth multi-track guard (v1.1.31) ===" -ForegroundColor Cyan
[void](Test-MultiTrackGuard)

# ---- Test 17: byte-boxing cast bug - "Specified cast is not valid" (v1.1.31 hotfix) ----
# Reproduces the v1.1.30 regression: when a real player is in sess.Drivers with
# GridPosition > 0 but is NOT classified by FC (e.g. FC row.Position=0 -> skipped
# at the "row.Position <= 0 continue" guard), the fallback path at lines 1157-1194
# adds them to resultsOut. The bug: `(object)dr.GridPosition` boxed the byte field
# directly, and ComputeAwards / "Most Positions Gained" then did `(int)gridObj`
# which throws InvalidCastException with the exact message "Specified cast is not
# valid". The user-facing symptom: auto-export silently failed at race end and
# manual Export showed the error. Fix: explicit (int) cast at the writer + defensive
# Convert.ToInt32 at the readers.
function Test-ByteBoxingCastBug() {
    $st0 = [System.Activator]::CreateInstance($storeType)

    # Online Race @ Monaco
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 5
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp))))

    # Participants: 2 active humans, Hamilton (ci=0) + Verstappen (ci=1)
    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 2
    # ci=0 Hamilton
    $pp[1] = 0           # AiControlled=false
    $pp[4] = 0           # TeamId=Mercedes
    $pp[6] = 44          # RaceNumber
    $n0 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($n0, 0, $pp, 8, $n0.Length)
    $pp[41] = 1; $pp[44] = 1   # ShowOnlineNames=on, Platform=Steam
    # ci=1 Verstappen
    $pp[58] = 0          # AiControlled=false
    $pp[61] = 2          # TeamId=Red Bull
    $pp[63] = 1          # RaceNumber
    $n1 = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
    [System.Array]::Copy($n1, 0, $pp, 65, $n1.Length)
    $pp[98] = 1; $pp[101] = 1
    for ($c = 2; $c -lt 22; $c++) {
        $st = 1 + $c * 57
        $pp[$st + 3] = 255; $pp[$st + 5] = 0
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp))))

    # LapData: both drivers on grid (GridPosition > 0). Hamilton on lap 1; Verstappen
    # also lap 1 then will DNF (no further laps recorded) - but his GridPosition byte
    # is set, which is exactly what later trips the cast bug.
    $ld1 = New-Object byte[] (22 * 57)
    $ld1[33] = 1; $ld1[33 + 57] = 1            # currentLapNum
    $ld1[43] = 1; $ld1[43 + 57] = 2            # gridPosition (BYTE)
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld1))))

    # Hamilton completes a couple of laps so he gets bestLap > 0 and Finished status
    $ld2 = New-Object byte[] (22 * 57)
    $ld2[33] = 2
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($ld2, 0)
    [System.BitConverter]::GetBytes([uint16]28000).CopyTo($ld2, 8)
    [System.BitConverter]::GetBytes([uint16]27000).CopyTo($ld2, 11)
    $ld2[43] = 1   # preserve grid
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld2))))

    # FC packet: ci=0 (Hamilton) Position=1, NumLaps=5, BestLap=83000ms.
    # ci=1 (Verstappen) Position=0 - the `row.Position <= 0` guard at the FC main
    # loop SKIPS this row, so Verstappen never enters resultsOut from there. He IS,
    # however, still in sess.Drivers (ParticipantsData created the bucket with
    # GridPosition=2 set via LapData). The fallback path at lines 1157-1194 picks
    # him up, and that's exactly where the byte-boxing happened.
    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 2
    # ci=0 Hamilton (offset 1)
    $fc[1] = 1            # Position=1
    $fc[2] = 5            # NumLaps=5
    $fc[3] = 1            # GridPosition (FC row, byte) - explicit (int) at writer, safe
    $fc[5] = 0            # NumPitStops
    $fc[6] = 3            # ResultStatus=Finished
    [System.BitConverter]::GetBytes([uint32]83000).CopyTo($fc, 1 + 7)   # BestLapTimeMs
    # ci=1 Verstappen (offset 47): Position=0 -> skipped by FC main loop
    $offV = 1 + 46
    $fc[$offV + 0] = 0    # Position=0 (key trigger)
    $fc[$offV + 1] = 0    # NumLaps=0
    $fc[$offV + 2] = 2    # GridPosition (irrelevant, row skipped)
    $fc[$offV + 6] = 4    # ResultStatus=DidNotFinish
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 8 $fc))))

    # Sanity: Verstappen must be in sess.Drivers with GridPosition > 0 BEFORE Finalize
    $sessDict = Get-Field $st0 "Sessions"
    $verPreFinalize = $false
    $verGrid = 0
    foreach ($k in $sessDict.Keys) {
        $sess = $sessDict[$k]
        $drvs = Get-Field $sess "Drivers"
        if ($drvs.ContainsKey("Verstappen")) {
            $verPreFinalize = $true
            $verGrid = (Get-Field $drvs["Verstappen"] "GridPosition")
        }
    }
    Assert "v1.1.31 hotfix: pre-Finalize sess.Drivers contains Verstappen (DNF candidate)" $verPreFinalize
    Assert "v1.1.31 hotfix: Verstappen GridPosition is byte > 0 (cast bug trigger)" ($verGrid -gt 0)

    # The crucial assertion: Finalize MUST NOT throw. Without the fix, this call
    # throws TargetInvocationException -> InvalidCastException("Specified cast is
    # not valid.") originating at LeagueFinalizer.ComputeAwards / Most Positions
    # Gained when (int)gridObj unboxes a Byte.
    $finalizeThrew = $false
    $finalizeErr = $null
    $res = $null
    try {
        $res = $finalizeMethod.Invoke($null, @($st0))
    } catch {
        $finalizeThrew = $true
        $finalizeErr = $_
        # Also unwrap to surface the real cause for diagnostic clarity
        $inner = $_.Exception
        while ($inner.InnerException -ne $null) { $inner = $inner.InnerException }
        Write-Host ("    -> Finalize threw: {0}: {1}" -f $inner.GetType().FullName, $inner.Message) -ForegroundColor Yellow
    }
    Assert "v1.1.31 hotfix: Finalize completes without throwing InvalidCastException" (-not $finalizeThrew)
    if ($finalizeThrew) { return }

    # And the Verstappen fallback row must be present (preserved as DNF), proving
    # the fallback path actually ran and the byte-boxing site was exercised.
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.31 hotfix: Finalize produced 1 session" ($sessions.Count -eq 1)
    if ($sessions.Count -lt 1) { return }
    $rs = Get-DictValue $sessions[0] "results"
    Assert "v1.1.31 hotfix: results array exists" ($rs -ne $null)
    if ($rs -eq $null) { return }

    $verRow = $null
    $hamRow = $null
    foreach ($r in $rs) {
        $tg = Get-DictValue $r "tag"
        if ($tg -eq "Verstappen") { $verRow = $r }
        if ($tg -eq "Hamilton")   { $hamRow = $r }
    }
    Assert "v1.1.31 hotfix: Hamilton classified by FC main loop"  ($hamRow -ne $null)
    Assert "v1.1.31 hotfix: Verstappen preserved by fallback path (real player, was DNF)" ($verRow -ne $null)

    # And his grid must now be a *plain Int32* boxed, not Byte - directly verifying
    # the fix at LeagueFinalizer line 1183.
    if ($verRow -ne $null) {
        $verGridObj = Get-DictValue $verRow "grid"
        Assert "v1.1.31 hotfix: Verstappen grid stored as Int32 (not Byte)" `
            ($verGridObj -ne $null -and $verGridObj.GetType() -eq [int])
    }

    # Awards must compute "mostPositionsGained" without crashing (was the throw site).
    $awards = Get-DictValue $sessions[0] "awards"
    Assert "v1.1.31 hotfix: awards object computed" ($awards -ne $null)
    if ($awards -ne $null) {
        Assert "v1.1.31 hotfix: mostPositionsGained key present" ($awards.ContainsKey("mostPositionsGained"))
    }
}

Write-Host "=== Test 17: byte-boxing cast bug regression (v1.1.31 hotfix) ===" -ForegroundColor Cyan
[void](Test-ByteBoxingCastBug)

# ---- Test 18: Monaco-style ghost via stale HumanCarIdxs latch (v1.1.32) ----
# Reproduces Monaco_20260510 ci=19 (Williams #23): F1 25 sent an early Participants
# packet for an AI grid filler with AiControlled=false + Platform!=255, latching
# HumanCarIdxs[2]=true. Later packets correctly flipped to AiControlled=true,
# but the sticky human flag let the slot escape every upstream phantom filter
# (IsKnownRealPlayer returned true via wasHuman, short-circuiting the AI check
# in IsPhantomEntry / ShouldSkipFcAiGridFillerRow). The Camada 6 post-filter
# + IsKnownRealPlayer hardening must drop this row.
#
# SAFETY assertion: the same test ALSO ensures Hamilton (real human, real laps)
# is preserved, proving the v1.1.30 invariant "real players never filtered" still
# holds under the new logic.
function Test-MonacoStyleGhost() {
    $st0 = [System.Activator]::CreateInstance($storeType)

    # Online race @ Monaco (trackId=5)
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 5
    $sp[124] = 0; $sp[125] = 1   # NetworkGame=1 (online)
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp))))

    # FIRST Participants packet: 3 active. ci=0 Hamilton (human),
    # ci=1 Verstappen (human), ci=2 Williams "AI grid filler" but the F1 25 bug
    # reports it as AiControlled=false + Platform=Steam (latches HumanCarIdxs[2]=true).
    $pp1 = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp1.Length; $zi++) { $pp1[$zi] = 0 }
    $pp1[0] = 3
    # ci=0 Hamilton: AI=false, team=Mercedes(0), rn=44, name, showOnlineNames=on, platform=Steam
    $pp1[1] = 0; $pp1[4] = 0; $pp1[6] = 44
    $hn = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($hn, 0, $pp1, 8, $hn.Length)
    $pp1[41] = 1; $pp1[44] = 1
    # ci=1 Verstappen
    $pp1[58] = 0; $pp1[61] = 2; $pp1[63] = 1
    $vn = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
    [System.Array]::Copy($vn, 0, $pp1, 65, $vn.Length)
    $pp1[98] = 1; $pp1[101] = 1
    # ci=2 BUGGY EARLY PACKET: Williams AI grid filler reported with AI=false + valid platform
    $pp1[115] = 0    # AiControlled=false  (the F1 25 bug)
    $pp1[118] = 3    # TeamId=Williams
    $pp1[120] = 23   # RaceNumber=23
    # No name (left zero) - simulates AI slot. Platform=Steam to latch HumanCarIdxs[2]=true.
    $pp1[155] = 0    # ShowOnlineNames=off
    $pp1[158] = 1    # Platform=Steam (with AI=false → triggers HumanCarIdxs[2]=true latch)
    for ($c = 3; $c -lt 22; $c++) {
        $st = 1 + $c * 57
        $pp1[$st + 3] = 255; $pp1[$st + 5] = 0
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp1))))

    # LapData: only Hamilton + Verstappen actually drive. ci=2 stays at 0 laps.
    $ld1 = New-Object byte[] (22 * 57)
    $ld1[33] = 1; $ld1[33 + 57] = 1
    $ld1[43] = 1; $ld1[43 + 57] = 2
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld1))))

    $ld2 = New-Object byte[] (22 * 57)
    $ld2[33] = 2; $ld2[33 + 57] = 2
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($ld2, 0)
    [System.BitConverter]::GetBytes([uint16]28000).CopyTo($ld2, 8)
    [System.BitConverter]::GetBytes([uint16]27000).CopyTo($ld2, 11)
    $ld2[43] = 1; $ld2[43 + 57] = 2
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld2))))

    # SECOND Participants packet: same 3 slots but ci=2 now CORRECTLY reports
    # AiControlled=true (the early bug got self-corrected later in the session).
    # HumanCarIdxs[2] stays true (sticky), but slot.AiControlled is now true.
    $pp2 = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp2.Length; $zi++) { $pp2[$zi] = $pp1[$zi] }
    $pp2[115] = 1   # AiControlled=true (corrected)
    $pp2[158] = 0   # Platform=255 (Unknown)
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp2))))

    # FC packet: ci=0 Hamilton P1 5 laps, ci=1 Verstappen P2 5 laps, ci=2 ghost P3 0 laps
    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 3
    # ci=0 Hamilton
    $fc[1] = 1; $fc[2] = 5; $fc[3] = 1; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]83000).CopyTo($fc, 1 + 7)
    # ci=1 Verstappen
    $offV = 1 + 46
    $fc[$offV + 0] = 2; $fc[$offV + 1] = 5; $fc[$offV + 2] = 2; $fc[$offV + 6] = 3
    [System.BitConverter]::GetBytes([uint32]84000).CopyTo($fc, $offV + 7)
    # ci=2 ghost: P3, 0 laps, NotClassified - this is the row that must be filtered.
    $offG = 1 + 2 * 46
    $fc[$offG + 0] = 3; $fc[$offG + 1] = 0; $fc[$offG + 2] = 3; $fc[$offG + 6] = 6
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 8 $fc))))

    $res = $finalizeMethod.Invoke($null, @($st0))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.32: session count is 1" ($sessions.Count -eq 1)
    if ($sessions.Count -lt 1) { return }

    $rs = Get-DictValue $sessions[0] "results"
    Assert "v1.1.32: results array exists" ($rs -ne $null)
    if ($rs -eq $null) { return }

    $hamFound = $false; $verFound = $false; $ghostFound = $false
    foreach ($r in $rs) {
        $tg = Get-DictValue $r "tag"
        if ($tg -eq "Hamilton")   { $hamFound = $true }
        if ($tg -eq "Verstappen") { $verFound = $true }
        # Ghost would appear as Driver_2 / Car_2 / Car2 (any generic placeholder for ci=2)
        if ($tg -eq "Driver_2" -or $tg -eq "Car_2" -or $tg -eq "Car2") { $ghostFound = $true }
    }
    Assert "v1.1.32: Hamilton preserved (real human, has laps)" $hamFound
    Assert "v1.1.32: Verstappen preserved (real human, has laps)" $verFound
    Assert "v1.1.32 GHOST FIX: ci=2 AI grid filler with stale HumanCarIdxs latch is FILTERED" (-not $ghostFound)
    Assert "v1.1.32: results count is exactly 2 (no ghost)" ($rs.Count -eq 2)

    # The IsKnownRealPlayer hardening typically catches ci=2 upstream
    # (RemovePhantomDrivers / ShouldSkipFcAiGridFillerRow), so Camada 6 stays
    # as a defense-in-depth net that doesn't always have to fire. Test 19
    # specifically guarantees Camada 6 never wrongly drops UNAcapeleto. The
    # CAMADA-6 note is therefore only ASSERTED if the row actually reached
    # the post-filter — we do not require it for the test to pass.
}

Write-Host "=== Test 18: Monaco-style ghost via stale HumanCarIdxs latch (v1.1.32) ===" -ForegroundColor Cyan
[void](Test-MonacoStyleGhost)

# ---- Test 19: UNAcapeleto-style invariant preservation under v1.1.32 hardening ----
# CRITICAL safety net: the v1.1.32 IsKnownRealPlayer hardening MUST NOT regress
# the v1.1.30 fix. A real player who:
#   - was in the lobby (lobby evidence)
#   - drove laps in qualifying
#   - disconnected before lap 1 in the race (0 laps in race)
#   - had his slot promoted to AI (slot.AiControlled=true at end of race)
# must STILL be preserved as DNF. Strong lobby evidence wins over the AI flag.
function Test-RealPlayerLobbyEvidenceVsAi() {
    $st0 = [System.Activator]::CreateInstance($storeType)

    # Online race @ LasVegas (trackId=20-style)
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 20
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp))))

    # LobbyInfo: 2 players, including UNAcapeleto (rn=74, tid=3)
    $lobbyPayload = New-Object byte[] (1 + 22 * 42)
    $lobbyPayload[0] = 2
    # ci=0 Hamilton lobby slot
    $lobbyPayload[1 + 0] = 0
    $lobbyPayload[1 + 1] = 0       # tid Mercedes
    $lobbyPayload[1 + 3] = 1
    $hn = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($hn, 0, $lobbyPayload, (1 + 4), $hn.Length)
    $lobbyPayload[1 + 36] = 44
    $lobbyPayload[1 + 38] = 1
    # ci=1 UNAcapeleto lobby slot (rn=74, tid=3)
    $offUna = 1 + 42
    $lobbyPayload[$offUna + 0] = 0
    $lobbyPayload[$offUna + 1] = 3
    $lobbyPayload[$offUna + 3] = 4
    $unaName = [System.Text.Encoding]::UTF8.GetBytes("UNAcapeleto")
    [System.Array]::Copy($unaName, 0, $lobbyPayload, ($offUna + 4), $unaName.Length)
    $lobbyPayload[$offUna + 36] = 74
    $lobbyPayload[$offUna + 38] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 9 $lobbyPayload))))

    # Participants: ci=0 Hamilton (human), ci=1 was UNAcapeleto but flipped to AI=true
    # at end of session (he disconnected, slot got promoted to AI grid filler)
    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 2
    # ci=0 Hamilton AI=false
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    [System.Array]::Copy($hn, 0, $pp, 8, $hn.Length)
    $pp[41] = 1; $pp[44] = 1
    # ci=1 UNAcapeleto: now AI=true (slot promoted), but lobby still has him!
    $pp[58] = 1     # AiControlled=true (CONTRADICTION with sticky-human, but lobby wins)
    $pp[61] = 3     # tid Williams (matches lobby)
    $pp[63] = 74    # rn=74 (matches lobby)
    [System.Array]::Copy($unaName, 0, $pp, 65, $unaName.Length)
    $pp[98] = 1; $pp[101] = 4   # showOnlineNames=on, platform=4
    for ($c = 2; $c -lt 22; $c++) {
        $st = 1 + $c * 57
        $pp[$st + 3] = 255; $pp[$st + 5] = 0
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp))))

    # LapData: only Hamilton drives (ci=1 abandons at start, 0 laps)
    $ld1 = New-Object byte[] (22 * 57)
    $ld1[33] = 1
    $ld1[43] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld1))))

    $ld2 = New-Object byte[] (22 * 57)
    $ld2[33] = 2
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($ld2, 0)
    $ld2[43] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld2))))

    # FC: ci=0 Hamilton P1, ci=1 UNAcapeleto P2 0 laps DNF
    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 2
    $fc[1] = 1; $fc[2] = 2; $fc[3] = 1; $fc[6] = 3
    $offU = 1 + 46
    $fc[$offU + 0] = 2; $fc[$offU + 1] = 0; $fc[$offU + 2] = 2; $fc[$offU + 6] = 4
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 8 $fc))))

    $res = $finalizeMethod.Invoke($null, @($st0))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.32 invariant: session exists" ($sessions.Count -ge 1)
    if ($sessions.Count -lt 1) { return }
    $rs = Get-DictValue $sessions[0] "results"
    Assert "v1.1.32 invariant: results exist" ($rs -ne $null)

    $unaFound = $false
    if ($rs -ne $null) {
        foreach ($r in $rs) {
            $tg = Get-DictValue $r "tag"
            if ($tg -eq "UNAcapeleto") { $unaFound = $true }
        }
    }
    Assert "v1.1.32 invariant: UNAcapeleto preserved despite slot.AiControlled=true (lobby evidence wins)" $unaFound
}

Write-Host "=== Test 19: real player + lobby evidence vs slot.AiControlled (v1.1.32 invariant) ===" -ForegroundColor Cyan
[void](Test-RealPlayerLobbyEvidenceVsAi)

# ---- Test 20: AI grid filler in same team as a single human (v1.1.33 Brazil regression) ----
# Reproduces Brazil_20260511_215148_531C9D.otk ci=19 (Visa Cash App #30):
# only Drako% (rn=73) was a Visa Cash App human in the lobby. F1 25 added an
# AI grid filler at ci=19 (rn=30) in the same team. The AI slot had:
#   - aiControlled=true
#   - 0 laps
#   - generic tag (Car_19 / Driver_19)
#   - NOT in lobbyNameMap (key 30_6 missing)
#   - NOT in bestKnownTags (key 30_6 missing)
#   - BUT lobbyByTeamOnly[6] = "Drako%" (single Visa Cash App human)
# v1.1.32 IsKnownRealPlayer used LookupBestKnownTagForEntry, which falls back
# to lobbyByTeamOnly[tid] when netKey + rnKey both miss — returned "Drako%"
# and treated the AI slot as a real player. v1.1.33 must use the STRICT
# lookup (netKey + rnKey only) so the AI grid filler is filtered while
# Drako% himself stays preserved.
function Test-AiFillerSameTeamAsSingleHuman() {
    $st0 = [System.Activator]::CreateInstance($storeType)

    # Online race @ Brazil (trackId=10)
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 10
    $sp[124] = 0; $sp[125] = 1   # NetworkGame=1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp))))

    # Lobby: 2 players, Hamilton (ci0 mapped) + Drako% (Visa Cash App, rn=73, tid=6).
    # Note: the AI grid filler that the GAME later adds is NOT in the lobby.
    $lobbyPayload = New-Object byte[] (1 + 22 * 42)
    $lobbyPayload[0] = 2
    # Lobby slot 0 = Hamilton, tid=Mercedes(0), rn=44
    $lobbyPayload[1 + 0] = 0; $lobbyPayload[1 + 1] = 0; $lobbyPayload[1 + 3] = 1
    $hn = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($hn, 0, $lobbyPayload, (1 + 4), $hn.Length)
    $lobbyPayload[1 + 36] = 44; $lobbyPayload[1 + 38] = 1
    # Lobby slot 1 = Drako%, tid=VisaCashApp(6), rn=73 (the ONLY Visa Cash App human)
    $offD = 1 + 42
    $lobbyPayload[$offD + 0] = 0; $lobbyPayload[$offD + 1] = 6; $lobbyPayload[$offD + 3] = 1
    $dn = [System.Text.Encoding]::UTF8.GetBytes("Drako%")
    [System.Array]::Copy($dn, 0, $lobbyPayload, ($offD + 4), $dn.Length)
    $lobbyPayload[$offD + 36] = 73; $lobbyPayload[$offD + 38] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 9 $lobbyPayload))))

    # Participants: ci=0 Hamilton (human), ci=1 Drako% (human, Visa Cash App #73),
    # ci=2 GHOST (AI grid filler in Visa Cash App, rn=30 — different rn from Drako%).
    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 3
    # ci=0 Hamilton AI=false, tid=0, rn=44
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    [System.Array]::Copy($hn, 0, $pp, 8, $hn.Length)
    $pp[41] = 1; $pp[44] = 1
    # ci=1 Drako% AI=false, tid=6 (VisaCashApp), rn=73
    $pp[58] = 0; $pp[61] = 6; $pp[63] = 73
    [System.Array]::Copy($dn, 0, $pp, 65, $dn.Length)
    $pp[98] = 1; $pp[101] = 1
    # ci=2 GHOST: AiControlled=true, tid=6 (VisaCashApp), rn=30 - NOT in lobby.
    $pp[115] = 1     # AiControlled=true
    $pp[118] = 6     # TeamId=VisaCashApp (same as Drako%)
    $pp[120] = 30    # RaceNumber=30 (different from Drako%'s 73)
    # No name (left zero) - simulates AI slot
    $pp[155] = 0     # ShowOnlineNames=off
    $pp[158] = 255   # Platform=Unknown
    for ($c = 3; $c -lt 22; $c++) {
        $st = 1 + $c * 57
        $pp[$st + 3] = 255; $pp[$st + 5] = 0
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp))))

    # LapData: only Hamilton + Drako% drive
    $ld1 = New-Object byte[] (22 * 57)
    $ld1[33] = 1; $ld1[33 + 57] = 1
    $ld1[43] = 1; $ld1[43 + 57] = 2
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld1))))

    $ld2 = New-Object byte[] (22 * 57)
    $ld2[33] = 2; $ld2[33 + 57] = 2
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($ld2, 0)
    [System.BitConverter]::GetBytes([uint16]28000).CopyTo($ld2, 8)
    [System.BitConverter]::GetBytes([uint16]27000).CopyTo($ld2, 11)
    $ld2[43] = 1; $ld2[43 + 57] = 2
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld2))))

    # FC: ci=0 Hamilton P1, ci=1 Drako% P2, ci=2 ghost P3 0 laps
    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 3
    $fc[1] = 1; $fc[2] = 5; $fc[3] = 1; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]83000).CopyTo($fc, 1 + 7)
    $offV = 1 + 46
    $fc[$offV + 0] = 2; $fc[$offV + 1] = 5; $fc[$offV + 2] = 2; $fc[$offV + 6] = 3
    [System.BitConverter]::GetBytes([uint32]84000).CopyTo($fc, $offV + 7)
    $offG = 1 + 2 * 46
    $fc[$offG + 0] = 3; $fc[$offG + 1] = 0; $fc[$offG + 2] = 3; $fc[$offG + 6] = 6
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 8 $fc))))

    $res = $finalizeMethod.Invoke($null, @($st0))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.33: session count is 1" ($sessions.Count -eq 1)
    if ($sessions.Count -lt 1) { return }
    $rs = Get-DictValue $sessions[0] "results"
    Assert "v1.1.33: results array exists" ($rs -ne $null)
    if ($rs -eq $null) { return }

    $hamFound = $false; $drakoFound = $false; $ghostFound = $false; $drakoCount = 0
    foreach ($r in $rs) {
        $tg = Get-DictValue $r "tag"
        if ($tg -eq "Hamilton") { $hamFound = $true }
        if ($tg -eq "Drako%")   { $drakoFound = $true; $drakoCount++ }
        if ($tg -eq "Driver_2" -or $tg -eq "Car_2" -or $tg -eq "Car2") { $ghostFound = $true }
    }
    Assert "v1.1.33: Hamilton preserved (real human, has laps)" $hamFound
    Assert "v1.1.33: Drako% preserved EXACTLY ONCE (real Visa Cash App human)" ($drakoFound -and $drakoCount -eq 1)
    Assert "v1.1.33 BRAZIL FIX: AI grid filler in same team as single human is FILTERED (no team-only inheritance)" (-not $ghostFound)
    Assert "v1.1.33: results count is exactly 2 (no ghost)" ($rs.Count -eq 2)

    # Confirm participants[] global also reflects the fix
    $globalParts = Get-DictValue $res "participants"
    Assert "v1.1.33: global participants count is 2 (only humans)" ($globalParts.Count -eq 2)
}

Write-Host "=== Test 20: AI grid filler in same team as single human (v1.1.33 Brazil regression) ===" -ForegroundColor Cyan
[void](Test-AiFillerSameTeamAsSingleHuman)

# ---- Test 21: ERS telemetry aggregation (v1.1.34) ----
# Synthesizes a 3-lap race for two drivers (ci=0 Hamilton, ci=1 Verstappen).
# Each CarStatus packet carries fuel + ERS bytes; we step time between packets
# by mutating internal _nowMs via the IngestCarStatusPublicForTest helper if
# available, otherwise we rely on real elapsed time which makes asserts on
# storePctAvg fuzzy. To keep the test deterministic we run a short burst of
# samples per "lap" inside a sleep-free loop, exercising the lap-rollover
# detection (counter drop) but accepting that storePctAvg is computed only
# from samples seen between calls (dtMs >= 1) — we therefore assert the AVG is
# within a wide tolerance around the simple mean.
function Test-ErsTelemetry() {
    $st0 = [System.Activator]::CreateInstance($storeType)

    # Race @ Bahrain, online
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 4; $sp[7] = 10
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp))))

    # Participants: ci=0 Hamilton (human), ci=1 Verstappen (human)
    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 2
    $hn = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($hn, 0, $pp, 8, $hn.Length)
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    $pp[41] = 1; $pp[44] = 1
    $vn = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
    [System.Array]::Copy($vn, 0, $pp, 65, $vn.Length)
    $pp[58] = 0; $pp[61] = 2; $pp[63] = 1
    $pp[98] = 1; $pp[101] = 1
    for ($c = 2; $c -lt 22; $c++) {
        $st = 1 + $c * 57
        $pp[$st + 3] = 255
        $pp[$st + 5] = 0
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp))))

    # Build a CarStatus packet (22 entries * 55 bytes). For each entry we set
    # TC/ABS/FuelMix/FuelCapacity sentinels valid for fuel capture, plus ERS
    # bytes at offsets 29..54.
    function New-CarStatusPacket($storeEnergyHam, $deployedHam, $hMgukHam, $hMguhHam, $modeHam, $storeEnergyVer, $deployedVer, $hMgukVer, $hMguhVer, $modeVer, $pausedHam = 0) {
        $cs = New-Object byte[] (22 * 55)
        for ($i = 0; $i -lt 22; $i++) {
            $off = $i * 55
            $cs[$off + 0] = 1
            $cs[$off + 1] = 1
            $cs[$off + 2] = 1
            [System.BitConverter]::GetBytes([float]100.0).CopyTo($cs, $off + 5)
            [System.BitConverter]::GetBytes([float]110.0).CopyTo($cs, $off + 9)
            [System.BitConverter]::GetBytes([float]50.0).CopyTo($cs, $off + 13)
        }
        $off0 = 0
        [System.BitConverter]::GetBytes([float]$storeEnergyHam).CopyTo($cs, $off0 + 37)
        $cs[$off0 + 41] = [byte]$modeHam
        [System.BitConverter]::GetBytes([float]$hMgukHam).CopyTo($cs, $off0 + 42)
        [System.BitConverter]::GetBytes([float]$hMguhHam).CopyTo($cs, $off0 + 46)
        [System.BitConverter]::GetBytes([float]$deployedHam).CopyTo($cs, $off0 + 50)
        $cs[$off0 + 54] = [byte]$pausedHam
        $off1 = 55
        [System.BitConverter]::GetBytes([float]$storeEnergyVer).CopyTo($cs, $off1 + 37)
        $cs[$off1 + 41] = [byte]$modeVer
        [System.BitConverter]::GetBytes([float]$hMgukVer).CopyTo($cs, $off1 + 42)
        [System.BitConverter]::GetBytes([float]$hMguhVer).CopyTo($cs, $off1 + 46)
        [System.BitConverter]::GetBytes([float]$deployedVer).CopyTo($cs, $off1 + 50)
        return ,$cs
    }

    # Lap 1: deploy goes 0 -> 4_000_000 J (100%), harvested ramps 0 -> 2 MJ (50%)
    # Lap 2: counter resets, deploy 0 -> 3_800_000 J (95%)
    # Lap 3: counter resets, deploy 0 -> 3_600_000 J (90%), Hamilton paused once
    # Store energy oscillates 4 MJ -> 1 MJ -> 4 MJ (avg roughly 2.5 MJ = 62.5%)

    # Sample bursts. Each call invokes Ingest -> IngestCarStatus with nowMs=NowMs()
    # internally. Time-weighted mean: dtMs between calls is real wall-clock time
    # (1-5ms in a tight loop), so all samples have small dt. That is enough for
    # the average to converge to the arithmetic mean of the sampled values.
    $samples = @(
        @(4000000, 0,       0,       0,       2,  4000000, 0,       0,       0,       2,  0),
        @(3000000, 1000000, 500000,  400000,  2,  3500000, 500000,  300000,  200000,  2,  0),
        @(2000000, 2000000, 800000,  700000,  3,  3000000, 1500000, 700000,  600000,  3,  0),
        @(1500000, 3000000, 1500000, 1300000, 3,  2500000, 2500000, 1300000, 1100000, 3,  0),
        @(1000000, 4000000, 2000000, 1800000, 3,  2000000, 3500000, 1800000, 1600000, 3,  0),
        # Lap 2 rollover (deployed drops)
        @(4000000, 100000,  100000,  100000,  1,  4000000, 100000,  100000,  100000,  1,  0),
        @(3000000, 1900000, 900000,  800000,  2,  3500000, 1500000, 700000,  600000,  2,  0),
        @(1500000, 3800000, 1600000, 1400000, 3,  2000000, 3000000, 1400000, 1200000, 3,  0),
        # Lap 3 rollover
        @(4000000, 200000,  200000,  200000,  1,  4000000, 200000,  200000,  200000,  1,  1),  # Hamilton paused this sample
        @(2500000, 1800000, 800000,  700000,  2,  3000000, 1400000, 700000,  600000,  2,  0),
        @(1000000, 3600000, 1400000, 1300000, 3,  2500000, 2800000, 1300000, 1100000, 3,  0)
    )

    foreach ($row in $samples) {
        $pkt = New-CarStatusPacket $row[0] $row[1] $row[2] $row[3] $row[4] $row[5] $row[6] $row[7] $row[8] $row[9] $row[10]
        $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 7 $pkt))))
    }

    # FC: both finish
    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 2
    $fc[1] = 1; $fc[2] = 5; $fc[3] = 1; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]83000).CopyTo($fc, 1 + 7)
    $offV = 1 + 46
    $fc[$offV + 0] = 2; $fc[$offV + 1] = 5; $fc[$offV + 2] = 2; $fc[$offV + 6] = 3
    [System.BitConverter]::GetBytes([uint32]84000).CopyTo($fc, $offV + 7)
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 8 $fc))))

    $res = $finalizeMethod.Invoke($null, @($st0))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.34: session count = 1" ($sessions.Count -eq 1)
    if ($sessions.Count -lt 1) { return }
    $drivers = Get-DictValue $sessions[0] "drivers"
    Assert "v1.1.34: drivers map present" ($drivers -ne $null)
    if ($drivers -eq $null) { return }

    $ham = $drivers["Hamilton"]
    Assert "v1.1.34: Hamilton driver present" ($ham -ne $null)
    if ($ham -eq $null) { return }

    $ers = Get-DictValue $ham "ersTelemetry"
    Assert "v1.1.34: Hamilton ersTelemetry present" ($ers -ne $null)
    if ($ers -eq $null) { return }

    $storeFirst = Get-DictValue $ers "storePctFirst"
    $storeLast = Get-DictValue $ers "storePctLast"
    $storeMin = Get-DictValue $ers "storePctMin"
    $storeMax = Get-DictValue $ers "storePctMax"
    $storeAvg = Get-DictValue $ers "storePctAvg"
    $deployedArr = Get-DictValue $ers "deployedPctPerLap"
    $deployedAvg = Get-DictValue $ers "deployedPctAvgPerLap"
    $harvMguk = Get-DictValue $ers "harvestedMgukPctPerLap"
    $harvMguh = Get-DictValue $ers "harvestedMguhPctPerLap"
    $harvMgukAvg = Get-DictValue $ers "harvestedMgukPctAvgPerLap"
    $harvMguhAvg = Get-DictValue $ers "harvestedMguhPctAvgPerLap"
    $modeLast = Get-DictValue $ers "deployModeLast"
    $samplesCount = Get-DictValue $ers "samplesCount"
    $samplesPaused = Get-DictValue $ers "samplesPaused"

    Assert "v1.1.34: storePctFirst == 100" ([Math]::Abs([double]$storeFirst - 100.0) -lt 0.5)
    Assert "v1.1.34: storePctMax == 100" ([Math]::Abs([double]$storeMax - 100.0) -lt 0.5)
    Assert "v1.1.34: storePctMin == 25 (1 MJ = 25%)" ([Math]::Abs([double]$storeMin - 25.0) -lt 0.5)
    Assert "v1.1.34: storePctLast == 25" ([Math]::Abs([double]$storeLast - 25.0) -lt 0.5)
    Assert "v1.1.34: storePctAvg in (40,70) range" ([double]$storeAvg -gt 40 -and [double]$storeAvg -lt 70)

    # deployedPctPerLap: rollover detection should produce three closed laps:
    # lap1 max 100, lap2 max 95, lap3 max 90 (in-flight at finalize -> snapshot pushed)
    Assert "v1.1.34: deployedPctPerLap has 3 entries (rollover + in-flight)" ($deployedArr.Count -eq 3)
    if ($deployedArr.Count -eq 3) {
        Assert "v1.1.34: deployedPctPerLap[0] == 100" ([Math]::Abs([double]$deployedArr[0] - 100.0) -lt 0.5)
        Assert "v1.1.34: deployedPctPerLap[1] == 95"  ([Math]::Abs([double]$deployedArr[1] - 95.0)  -lt 0.5)
        Assert "v1.1.34: deployedPctPerLap[2] == 90"  ([Math]::Abs([double]$deployedArr[2] - 90.0)  -lt 0.5)
    }
    Assert "v1.1.34: deployedPctAvgPerLap == 95" ([Math]::Abs([double]$deployedAvg - 95.0) -lt 0.5)

    Assert "v1.1.34: harvestedMgukPctPerLap has 3 entries" ($harvMguk.Count -eq 3)
    Assert "v1.1.34: harvestedMguhPctPerLap has 3 entries" ($harvMguh.Count -eq 3)

    # MGU-K samples for Hamilton: lap1 max 50% of store (2 MJ), lap2 40% (1.6 MJ),
    # lap3 35% (1.4 MJ). Mean store-relative = 41.67% -> 1.667 MJ.
    # v1.1.42 recalibration (2025 wire format): the % is now relative to the
    # MGU-K harvest CEILING (2 MJ in 2025), so 1.667 / 2 * 100 = 83.33%.
    # The new MJ field carries the absolute value (1.667 MJ).
    # MGU-H stays %-of-store: mean = (45+35+32.5)/3 = 37.5%.
    Assert "v1.1.35: harvestedMgukPctAvgPerLap present" ($harvMgukAvg -ne $null)
    Assert "v1.1.35: harvestedMguhPctAvgPerLap present" ($harvMguhAvg -ne $null)
    Assert "v1.1.42: harvestedMgukPctAvgPerLap == 83.33 (% of 2 MJ ceiling)" ([Math]::Abs([double]$harvMgukAvg - 83.33) -lt 0.6)
    Assert "v1.1.42: harvestedMgukMjAvgPerLap == 1.667 MJ" ([Math]::Abs([double](Get-DictValue $ers "harvestedMgukMjAvgPerLap") - 1.667) -lt 0.02)
    Assert "v1.1.35: harvestedMguhPctAvgPerLap == 37.5" ([Math]::Abs([double]$harvMguhAvg - 37.5) -lt 0.5)
    Assert "v1.1.42: harvestedMgukPctAvgPerLap <= 100 (regulation ceiling)" ([double]$harvMgukAvg -le 100.0)
    Assert "v1.1.35: harvestedMguhPctAvgPerLap <= 100" ([double]$harvMguhAvg -le 100.0)
    # Old combined field must be gone (would have been 79.17 = 41.67+37.5)
    $oldHarv = Get-DictValue $ers "harvestedPctAvgPerLap"
    Assert "v1.1.35: legacy harvestedPctAvgPerLap removed" ($oldHarv -eq $null)

    Assert "v1.1.34: deployModeLast == Overtake" ($modeLast -eq "Overtake")
    Assert "v1.1.34: samplesPaused == 1 (Hamilton paused once)" ([int]$samplesPaused -eq 1)
    Assert "v1.1.34: samplesCount >= 10" ([int]$samplesCount -ge 10)

    # Verstappen never paused
    $ver = $drivers["Verstappen"]
    if ($ver -ne $null) {
        $ersV = Get-DictValue $ver "ersTelemetry"
        if ($ersV -ne $null) {
            $sV = Get-DictValue $ersV "samplesPaused"
            Assert "v1.1.34: Verstappen samplesPaused == 0" ([int]$sV -eq 0)
        }
    }

    # Schema bump
    $schema = Get-DictValue $res "schemaVersion"
    Assert "v1.1.34: schemaVersion == league-1.1" ($schema -eq "league-1.1")
}

Write-Host "=== Test 21: ERS telemetry aggregation (v1.1.34) ===" -ForegroundColor Cyan
[void](Test-ErsTelemetry)

# ---- Test 22: phantom invariant preserved with ERS payload (v1.1.34) ----
# Re-runs the Brazil scenario from Test 20 but each CarStatus carries a full
# ERS payload for the AI grid filler (ci=2 Visa Cash App rn=30, paired with the
# only Visa Cash App human Drako% at rn=73). The strict lookup must STILL
# filter the AI; the ERS payload from the AI car must NOT be enough to flip
# IsKnownRealPlayer. This locks in the invariant that ERS is purely additive
# and never affects phantom filtering.
function Test-PhantomInvariantWithErsPayload() {
    $st0 = [System.Activator]::CreateInstance($storeType)

    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 10
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 1 $sp))))

    $lobbyPayload = New-Object byte[] (1 + 22 * 42)
    $lobbyPayload[0] = 2
    $lobbyPayload[1 + 0] = 0; $lobbyPayload[1 + 1] = 0; $lobbyPayload[1 + 3] = 1
    $hn = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($hn, 0, $lobbyPayload, (1 + 4), $hn.Length)
    $lobbyPayload[1 + 36] = 44; $lobbyPayload[1 + 38] = 1
    $offD = 1 + 42
    $lobbyPayload[$offD + 0] = 0; $lobbyPayload[$offD + 1] = 6; $lobbyPayload[$offD + 3] = 1
    $dn = [System.Text.Encoding]::UTF8.GetBytes("Drako%")
    [System.Array]::Copy($dn, 0, $lobbyPayload, ($offD + 4), $dn.Length)
    $lobbyPayload[$offD + 36] = 73; $lobbyPayload[$offD + 38] = 1
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 9 $lobbyPayload))))

    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 3
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    [System.Array]::Copy($hn, 0, $pp, 8, $hn.Length)
    $pp[41] = 1; $pp[44] = 1
    $pp[58] = 0; $pp[61] = 6; $pp[63] = 73
    [System.Array]::Copy($dn, 0, $pp, 65, $dn.Length)
    $pp[98] = 1; $pp[101] = 1
    $pp[115] = 1; $pp[118] = 6; $pp[120] = 30
    $pp[155] = 0; $pp[158] = 255
    for ($c = 3; $c -lt 22; $c++) {
        $st = 1 + $c * 57
        $pp[$st + 3] = 255; $pp[$st + 5] = 0
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 4 $pp))))

    # CarStatus with ERS payload populated for ALL cars including ci=2 AI.
    $cs = New-Object byte[] (22 * 55)
    for ($i = 0; $i -lt 22; $i++) {
        $off = $i * 55
        $cs[$off + 0] = 1
        $cs[$off + 1] = 1
        $cs[$off + 2] = 1
        [System.BitConverter]::GetBytes([float]100.0).CopyTo($cs, $off + 5)
        [System.BitConverter]::GetBytes([float]110.0).CopyTo($cs, $off + 9)
        [System.BitConverter]::GetBytes([float]50.0).CopyTo($cs, $off + 13)
        # ERS payload — even the AI ghost gets a full battery telemetry
        [System.BitConverter]::GetBytes([float]2000000.0).CopyTo($cs, $off + 37)
        $cs[$off + 41] = 2
        [System.BitConverter]::GetBytes([float]500000.0).CopyTo($cs, $off + 42)
        [System.BitConverter]::GetBytes([float]400000.0).CopyTo($cs, $off + 46)
        [System.BitConverter]::GetBytes([float]1000000.0).CopyTo($cs, $off + 50)
        $cs[$off + 54] = 0
    }
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 7 $cs))))

    $ld1 = New-Object byte[] (22 * 57)
    $ld1[33] = 1; $ld1[33 + 57] = 1
    $ld1[43] = 1; $ld1[43 + 57] = 2
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld1))))

    $ld2 = New-Object byte[] (22 * 57)
    $ld2[33] = 2; $ld2[33 + 57] = 2
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($ld2, 0)
    [System.BitConverter]::GetBytes([uint16]28000).CopyTo($ld2, 8)
    [System.BitConverter]::GetBytes([uint16]27000).CopyTo($ld2, 11)
    $ld2[43] = 1; $ld2[43 + 57] = 2
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 2 $ld2))))

    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 3
    $fc[1] = 1; $fc[2] = 5; $fc[3] = 1; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]83000).CopyTo($fc, 1 + 7)
    $offV = 1 + 46
    $fc[$offV + 0] = 2; $fc[$offV + 1] = 5; $fc[$offV + 2] = 2; $fc[$offV + 6] = 3
    [System.BitConverter]::GetBytes([uint32]84000).CopyTo($fc, $offV + 7)
    $offG = 1 + 2 * 46
    $fc[$offG + 0] = 3; $fc[$offG + 1] = 0; $fc[$offG + 2] = 3; $fc[$offG + 6] = 6
    $ingestMethod.Invoke($st0, @((Dispatch (New-FakePacket 8 $fc))))

    $res = $finalizeMethod.Invoke($null, @($st0))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.34: session count = 1 (with ERS payload)" ($sessions.Count -eq 1)
    if ($sessions.Count -lt 1) { return }
    $rs = Get-DictValue $sessions[0] "results"
    Assert "v1.1.34: results present" ($rs -ne $null)
    if ($rs -eq $null) { return }

    $hamFound = $false; $drakoFound = $false; $ghostFound = $false
    foreach ($r in $rs) {
        $tg = Get-DictValue $r "tag"
        if ($tg -eq "Hamilton") { $hamFound = $true }
        if ($tg -eq "Drako%")   { $drakoFound = $true }
        if ($tg -eq "Driver_2" -or $tg -eq "Car_2" -or $tg -eq "Car2") { $ghostFound = $true }
    }
    Assert "v1.1.34 INVARIANT: Hamilton preserved even with ERS payload" $hamFound
    Assert "v1.1.34 INVARIANT: Drako% preserved even with ERS payload" $drakoFound
    Assert "v1.1.34 INVARIANT: AI grid filler STILL filtered with ERS payload (ERS is not evidence)" (-not $ghostFound)
    Assert "v1.1.34 INVARIANT: results count = 2" ($rs.Count -eq 2)
}

Write-Host "=== Test 22: phantom invariant preserved with ERS payload (v1.1.34) ===" -ForegroundColor Cyan
[void](Test-PhantomInvariantWithErsPayload)

function Test-GameVersionDynamicLabel() {
    # v1.1.36 — F1 26 readiness. The exported `game` field must now follow the
    # PacketFormat captured in the header, NOT be hard-coded to "F1_25".
    # We build three mini-stores, each fed a single packet whose header carries
    # a different PacketFormat, and assert the resolved label matches.
    $cases = @(
        @{ Fmt = [uint16]2025; Expected = "F1_25"; Label = "F1 25" },
        @{ Fmt = [uint16]2026; Expected = "F1_26"; Label = "F1 26" },
        @{ Fmt = [uint16]2030; Expected = "F1_2030"; Label = "unknown future format" }
    )

    foreach ($case in $cases) {
        $store2 = [System.Activator]::CreateInstance($storeType)

        # Build a header with the per-case PacketFormat. Body is a tiny
        # Session payload (packet id 1) just so SessionStore creates a session.
        $hdr = New-Object byte[] 29
        [System.BitConverter]::GetBytes($case.Fmt).CopyTo($hdr, 0)
        $hdr[2] = 25; $hdr[3] = 1; $hdr[4] = 0; $hdr[5] = 1
        $hdr[6] = 1   # packet id = Session
        [System.BitConverter]::GetBytes([uint64]999).CopyTo($hdr, 7)
        [System.BitConverter]::GetBytes([float]50.0).CopyTo($hdr, 15)
        [System.BitConverter]::GetBytes([uint32]1).CopyTo($hdr, 19)
        [System.BitConverter]::GetBytes([uint32]1).CopyTo($hdr, 23)
        $hdr[27] = 0; $hdr[28] = 255

        $body = New-Object byte[] 700
        $body[0] = 1; $body[1] = 25; $body[2] = 22; $body[6] = 10; $body[7] = 5
        $body[124] = 0; $body[125] = 1

        $full = New-Object byte[] ($hdr.Length + $body.Length)
        [System.Array]::Copy($hdr, 0, $full, 0, $hdr.Length)
        [System.Array]::Copy($body, 0, $full, $hdr.Length, $body.Length)

        $ingestMethod.Invoke($store2, @((Dispatch $full)))

        $out = $finalizeMethod.Invoke($null, @($store2))
        $game = Get-DictValue $out "game"
        Assert "v1.1.36 ($($case.Label)): game = $($case.Expected) (got '$game')" ($game -eq $case.Expected)

        $dbg = Get-DictValue $out "_debug"
        $dbgGame = Get-DictValue $dbg "game"
        Assert "v1.1.36 ($($case.Label)): _debug.game block emitted" ($dbgGame -ne $null)
        if ($dbgGame -ne $null) {
            $resolvedLabel = Get-DictValue $dbgGame "resolvedGameLabel"
            Assert "v1.1.36 ($($case.Label)): _debug.game.resolvedGameLabel = $($case.Expected)" ($resolvedLabel -eq $case.Expected)

            $maxCars = Get-DictValue $dbgGame "parserMaxSupportedCars"
            Assert "v1.1.36 ($($case.Label)): _debug.game.parserMaxSupportedCars >= 24" ([int]$maxCars -ge 24)

            $pf = Get-DictValue $dbgGame "packetFormat"
            Assert "v1.1.36 ($($case.Label)): _debug.game.packetFormat = $($case.Fmt)" ([int]$pf -eq [int]$case.Fmt)
        }
    }
}

Write-Host "=== Test 23: game label derived from PacketFormat (v1.1.36 F1 26 readiness) ===" -ForegroundColor Cyan
[void](Test-GameVersionDynamicLabel)

function Test-ExpandedGridParsing() {
    # v1.1.36 — Forward-compat with F1 26 grids (24 cars / 11 teams).
    # Build a Participants packet with 24 entries instead of 22 and verify
    # the parser reads all of them through the public ParticipantsData type.
    $partType = $asm.GetType("Overtake.SimHub.Plugin.Packets.ParticipantsData")

    # ParticipantsData header (29) + numActiveCars (1) + 26 entries * 57 stride.
    # We populate 24 active entries (matches expected F1 26 grid) and leave
    # slots 24, 25 empty in the buffer. MaxSupportedCars=26 must accept this.
    $entriesToFill = 24
    $bufSize = 29 + 1 + 26 * 57
    $buf = New-Object byte[] $bufSize

    # Header
    [System.BitConverter]::GetBytes([uint16]2026).CopyTo($buf, 0)
    $buf[2] = 26; $buf[3] = 1; $buf[4] = 0; $buf[5] = 1
    $buf[6] = 4   # Participants packet id
    [System.BitConverter]::GetBytes([uint64]4242).CopyTo($buf, 7)
    [System.BitConverter]::GetBytes([float]10.0).CopyTo($buf, 15)
    $buf[27] = 0; $buf[28] = 255

    $buf[29] = [byte]$entriesToFill   # numActiveCars = 24

    # Per-entry: position name like "Driver24A".."Driver24X" for uniqueness.
    for ($i = 0; $i -lt $entriesToFill; $i++) {
        $start = 30 + $i * 57
        $buf[$start + 0] = 0                  # aiControlled = false
        $buf[$start + 1] = 255                # DriverId = network
        $buf[$start + 3] = [byte](($i % 11))  # teamId 0..10 (Cadillac would be #10)
        $buf[$start + 4] = 0                  # myTeam
        $buf[$start + 5] = [byte]($i + 1)     # raceNumber unique
        $buf[$start + 40] = 1                 # ShowOnlineNames = 1
        $buf[$start + 43] = 1                 # Platform = Steam
        $name = [System.Text.Encoding]::UTF8.GetBytes("Test$i")
        [System.Array]::Copy($name, 0, $buf, $start + 7, $name.Length)
    }

    # v1.1.40: ParticipantsData.Parse now has two overloads (1-arg = 2025,
    # 2-arg = format-aware). Disambiguate to the single-byte[] overload.
    $parseMethod = $partType.GetMethod("Parse", [Type[]]@([byte[]]))
    $parts = $parseMethod.Invoke($null, [object[]]@(,[byte[]]$buf))

    Assert "v1.1.36: parser accepts 24-active-car packet" ($parts.NumActiveCars -eq 24)
    Assert "v1.1.36: Entries array sized to MaxSupportedCars (26)" ($parts.Entries.Length -eq 26)
    Assert "v1.1.36: 24th entry parsed (carIdx 23)" ($parts.Entries[23] -ne $null)
    Assert "v1.1.36: 24th entry name preserved" ($parts.Entries[23].Name -eq "Test23")
    Assert "v1.1.36: TagsByCarIdx contains 24th car" ($parts.TagsByCarIdx.ContainsKey(23))
}

Write-Host "=== Test 24: parsers accept 24-car grid (v1.1.36 F1 26 readiness) ===" -ForegroundColor Cyan
[void](Test-ExpandedGridParsing)

function Test-LapDataPartialBufferTolerated() {
    # v1.1.36 — LapData parser must tolerate buffers smaller than NumCars
    # (was strict: required exactly 22 * 57 bytes; now reads what fits).
    # We feed a buffer with exactly 22 entries (F1 25 grid) and confirm the
    # parser fills slots 0..21 and leaves 22..25 null without throwing.
    $lapType = $asm.GetType("Overtake.SimHub.Plugin.Packets.LapDataEntry")

    $buf = New-Object byte[] (29 + 57 * 22)
    [System.BitConverter]::GetBytes([uint16]2025).CopyTo($buf, 0)
    $buf[2] = 25; $buf[6] = 2
    [System.BitConverter]::GetBytes([uint64]1).CopyTo($buf, 7)
    [System.BitConverter]::GetBytes([float]1.0).CopyTo($buf, 15)
    $buf[27] = 0; $buf[28] = 255

    # Mark car 21 with a recognisable lap number so we can assert it parsed.
    $car21 = 29 + 21 * 57
    $buf[$car21 + 33] = 7   # currentLapNum = 7

    $parseMethod = $lapType.GetMethod("Parse")
    $rows = $parseMethod.Invoke($null, [object[]]@(,[byte[]]$buf))

    Assert "v1.1.36: LapData parses 22-car F1 25 buffer without error" ($rows -ne $null)
    Assert "v1.1.36: LapData array sized to MaxSupportedCars (26)" ($rows.Length -eq 26)
    Assert "v1.1.36: LapData slot 21 populated (CurrentLapNum=7)" ($rows[21] -ne $null -and $rows[21].CurrentLapNum -eq 7)
    Assert "v1.1.36: LapData slot 22 null (out of buffer)" ($rows[22] -eq $null)
    Assert "v1.1.36: LapData slot 25 null (out of buffer)" ($rows[25] -eq $null)
}

Write-Host "=== Test 25: LapData parser tolerant of partial buffers (v1.1.36) ===" -ForegroundColor Cyan
[void](Test-LapDataPartialBufferTolerated)

function Test-CarryOverFcOnlyPhantomFiltered() {
    # v1.1.37 -- SilverstoneReverse_20260518_224341 regression.
    # User started capture while a previous lobby's results screen was still
    # repeating Final Classification packets (~5s cadence). The plugin created
    # a SessionRun for that stale sessionUID, never received Session nor
    # Participants for it (so trackId / sessionType stayed null,
    # participantsPeakNumActive stayed 0), and populated 19 Car_X "drivers"
    # purely from the FC carry-over. The site rejected the .otk with
    # "Track(None)" because sessions[0].track.name was "Track(None)".
    #
    # Expected after fix: the carry-over session is dropped from sessions[]
    # AND the Car_X tags are evicted from the global participants[] list.
    $st = [System.Activator]::CreateInstance($storeType)

    # Phase 1: stale FC carry-over for sessionUID=100 (no Session, no Participants).
    # 19 finished cars -- replicates the user's previous race.
    $fcStale = New-Object byte[] (1 + 22 * 46)
    $fcStale[0] = 19
    for ($i = 0; $i -lt 19; $i++) {
        $off = 1 + $i * 46
        $fcStale[$off + 0] = [byte]($i + 1)   # Position
        $fcStale[$off + 1] = 30                # NumLaps
        $fcStale[$off + 6] = 3                 # ResultStatus = Finished
        [System.BitConverter]::GetBytes([uint32]90000).CopyTo($fcStale, $off + 7)
    }
    # Fire it 3 times to mimic the repeated FC stream the game emits.
    for ($k = 0; $k -lt 3; $k++) {
        $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fcStale ([uint64]100)))))
    }

    # Sanity: store now has the carry-over session, with 19 Car_X drivers,
    # sessionType null, trackId null, participantsPeak = 0.
    $sessDict = Get-Field $st "Sessions"
    Assert "v1.1.37: carry-over store has 1 session pre-Finalize" ($sessDict.Count -eq 1)
    $stale = $sessDict["100"]
    Assert "v1.1.37: carry-over session has no sessionType"           (-not (Get-Field $stale "SessionType").HasValue)
    Assert "v1.1.37: carry-over session has no trackId"               (-not (Get-Field $stale "TrackId").HasValue)
    Assert "v1.1.37: carry-over session has participantsPeak == 0"    ((Get-Field $stale "ParticipantsPeakNumActive") -eq 0)
    Assert "v1.1.37: carry-over session has drivers populated by FC"  ((Get-Field $stale "Drivers").Count -ge 1)

    # Phase 2: a real race for sessionUID=200 -- Session (Race @ trackId=10),
    # Participants (Hamilton + Verstappen), then FC. This is the legitimate one.
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 10
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp ([uint64]200)))))

    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 2
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    $n0 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($n0, 0, $pp, 8, $n0.Length)
    $pp[41] = 1; $pp[44] = 1
    $pp[58] = 0; $pp[61] = 2; $pp[63] = 1
    $n1 = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
    [System.Array]::Copy($n1, 0, $pp, 65, $n1.Length)
    $pp[98] = 1; $pp[101] = 1
    for ($c = 2; $c -lt 22; $c++) {
        $cst = 1 + $c * 57
        $pp[$cst + 3] = 255; $pp[$cst + 5] = 0
    }
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp ([uint64]200)))))

    $fcReal = New-Object byte[] (1 + 22 * 46)
    $fcReal[0] = 2
    $fcReal[1] = 1; $fcReal[2] = 30; $fcReal[6] = 3
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($fcReal, 1 + 7)
    $off2 = 1 + 46
    $fcReal[$off2 + 0] = 2; $fcReal[$off2 + 1] = 30; $fcReal[$off2 + 6] = 3
    [System.BitConverter]::GetBytes([uint32]85500).CopyTo($fcReal, $off2 + 7)
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fcReal ([uint64]200)))))

    # Finalize and verify carry-over was dropped.
    $res = $finalizeMethod.Invoke($null, @($st))

    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.37: Finalize emits exactly 1 real session (carry-over filtered)" ($sessions.Count -eq 1)

    # Global participants[] must NOT include any Car_X -- those came only from
    # the carry-over session, which was filtered before participants[] is rebuilt.
    $participants = Get-DictValue $res "participants"
    $hasCarX = $false
    $hasHam = $false; $hasVer = $false
    foreach ($p in $participants) {
        if ($p -like "Car_*") { $hasCarX = $true }
        if ($p -eq "Hamilton") { $hasHam = $true }
        if ($p -eq "Verstappen") { $hasVer = $true }
    }
    Assert "v1.1.37: global participants[] has NO Car_X tags" (-not $hasCarX)
    Assert "v1.1.37: real participants preserved (Hamilton)" $hasHam
    Assert "v1.1.37: real participants preserved (Verstappen)" $hasVer

    # _debug counter must reflect the drop, so we can spot this in the wild.
    $dbg = Get-DictValue $res "_debug"
    $integrity = Get-DictValue $dbg "integrity"
    $dropped = Get-DictValue $integrity "carryOverSessionsDropped"
    Assert "v1.1.37: _debug.integrity.carryOverSessionsDropped >= 1" ([int]$dropped -ge 1)
}

Write-Host "=== Test 26: FC-only carry-over phantom filtered (v1.1.37 SilverstoneReverse regression) ===" -ForegroundColor Cyan
[void](Test-CarryOverFcOnlyPhantomFiltered)

function Test-SprintFormatConsolidatorInvariant() {
    # v1.1.37 -- Lock-in test for the existing Sprint Format consolidator.
    # v1.1.38 -- IDs corrected against the official F1 25 UDP spec
    #            (Data Output from F1 25 v3.pdf, "Session types" appendix).
    # The invariant (since v1.1.31): SS + SQ + Sprint + Quali + Race within
    # the SAME track must end up in ONE consolidated .otk. Two pieces of
    # logic protect this:
    #   1. IsTerminalSession returns true only for "Race" -- so Sprint never
    #      triggers auto-export prematurely (it would split the file).
    #   2. Auto-rotation only fires when trackId changes AND a "Race"
    #      session with FC already exists. Sprint Format never changes
    #      trackId mid-weekend, so auto-rotation stays dormant.
    # If a future refactor breaks either piece, this test fails loudly.
    #
    # Session type ids per F1 25 UDP spec (Data Output from F1 25 v3.pdf):
    #   10 = Sprint Shootout 1 (used here for SS / Sprint Qualifying)
    #    8 = Short Qualifying  (used in some Sprint Format lobbies for SQ)
    #   16 = Race 2            (Sprint Race proper in Sprint Format weekends)
    #    5 = Qualifying 1      (main qualifying)
    #   15 = Race              (Main Race, the only terminal session id)
    $st = [System.Activator]::CreateInstance($storeType)
    $tid = 14   # Abu Dhabi -- any consistent trackId works

    # Helper closures using script-scope dispatch. We inline the packet
    # construction because PS function nesting + reflection gets fiddly.
    $feedSession = {
        param([uint64]$uid, [byte]$stype, [byte]$tidByte)
        $sp = New-Object byte[] 700
        $sp[0] = 1; $sp[6] = $stype; $sp[7] = $tidByte
        $sp[124] = 0; $sp[125] = 1
        $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp $uid))))
    }
    $feedParticipants = {
        param([uint64]$uid)
        $pp = New-Object byte[] 1256
        for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
        $pp[0] = 2
        $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
        $n0 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
        [System.Array]::Copy($n0, 0, $pp, 8, $n0.Length)
        $pp[41] = 1; $pp[44] = 1
        $pp[58] = 0; $pp[61] = 2; $pp[63] = 1
        $n1 = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
        [System.Array]::Copy($n1, 0, $pp, 65, $n1.Length)
        $pp[98] = 1; $pp[101] = 1
        for ($c = 2; $c -lt 22; $c++) {
            $cst = 1 + $c * 57
            $pp[$cst + 3] = 255; $pp[$cst + 5] = 0
        }
        $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp $uid))))
    }
    $feedFc = {
        param([uint64]$uid)
        $fc = New-Object byte[] (1 + 22 * 46)
        $fc[0] = 2
        $fc[1] = 1; $fc[2] = 5; $fc[6] = 3
        [System.BitConverter]::GetBytes([uint32]88000).CopyTo($fc, 1 + 7)
        $offX = 1 + 46
        $fc[$offX + 0] = 2; $fc[$offX + 1] = 5; $fc[$offX + 6] = 3
        [System.BitConverter]::GetBytes([uint32]88500).CopyTo($fc, $offX + 7)
        $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc $uid))))
    }

    # Sprint Shootout 1 (id=10) -- terminal? No (BUG in <= v1.1.37: was "Race").
    & $feedSession ([uint64]100) ([byte]10) ([byte]$tid)
    & $feedParticipants ([uint64]100)
    & $feedFc ([uint64]100)
    Assert "v1.1.38 Sprint invariant: SS (id=10 Sprint Shootout 1) + FC does NOT count as terminal" (-not $st.HasClosedTerminalSession())

    # Short Qualifying (id=8) -- terminal? No.
    & $feedSession ([uint64]101) ([byte]8) ([byte]$tid)
    & $feedParticipants ([uint64]101)
    & $feedFc ([uint64]101)
    Assert "v1.1.38 Sprint invariant: SQ (id=8) + FC does NOT count as terminal" (-not $st.HasClosedTerminalSession())

    # Sprint Race (id=16 Race 2) -- terminal? No (BUG in <= v1.1.37: was "Race").
    & $feedSession ([uint64]102) ([byte]16) ([byte]$tid)
    & $feedParticipants ([uint64]102)
    & $feedFc ([uint64]102)
    Assert "v1.1.38 Sprint invariant: Sprint Race (id=16 Race 2) + FC does NOT count as terminal" (-not $st.HasClosedTerminalSession())

    # Qualifying 1 (id=5) -- terminal? No.
    & $feedSession ([uint64]103) ([byte]5) ([byte]$tid)
    & $feedParticipants ([uint64]103)
    & $feedFc ([uint64]103)
    Assert "v1.1.38 Sprint invariant: Quali (id=5) + FC does NOT count as terminal" (-not $st.HasClosedTerminalSession())

    # Main Race (id=15) -- the ONLY terminal session per F1 25 spec.
    & $feedSession ([uint64]104) ([byte]15) ([byte]$tid)
    & $feedParticipants ([uint64]104)
    & $feedFc ([uint64]104)
    Assert "v1.1.38 Sprint invariant: Race (id=15) + FC IS terminal" $st.HasClosedTerminalSession()

    # No AUTO-ROTATE should have been requested at any point -- trackId never moved.
    $notes = Get-Field $st "Notes"
    $rotateNote = $false
    foreach ($n in $notes) { if ($n -like "*AUTO-ROTATE*") { $rotateNote = $true; break } }
    Assert "v1.1.37 Sprint invariant: NO AUTO-ROTATE during Sprint Format (trackId stable)" (-not $rotateNote)

    # Finalize must emit all 5 sessions consolidated in one file.
    $res = $finalizeMethod.Invoke($null, @($st))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.37 Sprint invariant: Finalize emits ALL 5 sessions (SS+SQ+Sprint+Quali+Race) in ONE file" ($sessions.Count -eq 5)
}

Write-Host "=== Test 27: Sprint Format consolidator invariant (v1.1.37 lock-in / v1.1.38 IDs) ===" -ForegroundColor Cyan
[void](Test-SprintFormatConsolidatorInvariant)

function Test-SprintShootoutId10NotTerminal() {
    # v1.1.38 -- direct regression for the Sprint Format bug reported in v1.1.37.
    # Before v1.1.38, Lookups.SessionType mapped id=10 to "Race", which made
    # IsTerminalSession(10) return TRUE and auto-export fire after the Sprint
    # Shootout 1's Final Classification. That split Sprint Format weekends into
    # separate .otk files. F1 25 spec is unambiguous: id=10 is "Sprint Shootout 1".
    #
    # This test directly poisons the store with id=10 + a Final Classification and
    # asserts HasClosedTerminalSession() stays FALSE -- meaning no auto-export
    # would have fired.
    $st = [System.Activator]::CreateInstance($storeType)

    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 10; $sp[7] = 14   # sessionType=10 (Sprint Shootout 1), Abu Dhabi
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp ([uint64]300)))))

    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 1
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    $nm = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($nm, 0, $pp, 8, $nm.Length)
    $pp[41] = 1; $pp[44] = 1
    for ($c = 1; $c -lt 22; $c++) {
        $cst = 1 + $c * 57
        $pp[$cst + 3] = 255; $pp[$cst + 5] = 0
    }
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp ([uint64]300)))))

    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 1
    $fc[1] = 1; $fc[2] = 3; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]87000).CopyTo($fc, 1 + 7)
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc ([uint64]300)))))

    Assert "v1.1.38 ID fix: id=10 (Sprint Shootout 1) + FC must NOT register as terminal session" `
        (-not $st.HasClosedTerminalSession())

    # Finalize and confirm Lookups labels the session correctly.
    $res = $finalizeMethod.Invoke($null, @($st))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.38 ID fix: id=10 session is emitted" ($sessions.Count -eq 1)
    if ($sessions.Count -eq 1) {
        $stype = Get-DictValue $sessions[0] "sessionType"
        $nm2 = Get-DictValue $stype "name"
        Assert "v1.1.38 ID fix: id=10 sessionType.name == 'SprintShootout1' (not 'Race')" `
            ($nm2 -eq "SprintShootout1")
    }
}

Write-Host "=== Test 28: Sprint Shootout 1 (id=10) NOT terminal (v1.1.38 ID fix) ===" -ForegroundColor Cyan
[void](Test-SprintShootoutId10NotTerminal)

function Test-SprintRaceId16NotTerminal() {
    # v1.1.38 -- F1 25 spec: id=16 is "Race 2", which in Sprint Format weekends
    # is the Sprint Race proper. Before v1.1.38, Lookups had id=16 -> "Race",
    # making IsTerminalSession(16) return TRUE and firing auto-export at the
    # Sprint Race's Final Classification -- so the Main Race that followed
    # would have to start a fresh capture and ended up in a separate .otk.
    #
    # After fix: id=16 -> "Race2", IsTerminalSession(16) == false. Sprint
    # Race waits for the Main Race (id=15) to fire auto-export.
    $st = [System.Activator]::CreateInstance($storeType)

    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 16; $sp[7] = 14   # sessionType=16 (Race 2 / Sprint Race), Abu Dhabi
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp ([uint64]400)))))

    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 1
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    $nm = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($nm, 0, $pp, 8, $nm.Length)
    $pp[41] = 1; $pp[44] = 1
    for ($c = 1; $c -lt 22; $c++) {
        $cst = 1 + $c * 57
        $pp[$cst + 3] = 255; $pp[$cst + 5] = 0
    }
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp ([uint64]400)))))

    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 1
    $fc[1] = 1; $fc[2] = 24; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]86500).CopyTo($fc, 1 + 7)
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc ([uint64]400)))))

    Assert "v1.1.38 ID fix: id=16 (Race 2 / Sprint Race) + FC must NOT register as terminal" `
        (-not $st.HasClosedTerminalSession())

    $res = $finalizeMethod.Invoke($null, @($st))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.38 ID fix: id=16 session is emitted" ($sessions.Count -eq 1)
    if ($sessions.Count -eq 1) {
        $stype = Get-DictValue $sessions[0] "sessionType"
        $nm2 = Get-DictValue $stype "name"
        Assert "v1.1.38 ID fix: id=16 sessionType.name == 'Race2' (not 'Race')" `
            ($nm2 -eq "Race2")
    }
}

Write-Host "=== Test 29: Sprint Race (id=16 Race 2) NOT terminal (v1.1.38 ID fix) ===" -ForegroundColor Cyan
[void](Test-SprintRaceId16NotTerminal)

function Test-CleanCaptureFullyResetsStoreNoDataLoss() {
    # v1.1.38 -- contract for the "clean session" pipeline. User reported concern:
    # "garanta que ao final da sessao a gente de fato consiga limpar os dados,
    # sem perder dados relevantes da corrida."
    #
    # The end-to-end pipeline is:
    #   1. Race + FC arrives -> IsTerminalSession(id=15) returns true
    #   2. Auto-export builds the .otk (LeagueFinalizer.Finalize, then OtkWriter)
    #   3. ONLY THEN BeginNewCapture() clears the store -- so the .otk has every
    #      relevant byte before the in-memory state is wiped.
    #
    # This test enforces the *clear* half of that contract: Finalize captures
    # everything we expected (drivers, results, events), and then BeginNewCapture
    # wipes the store such that an immediate second Finalize on the same store
    # returns zero sessions -- proving no stale residue would leak into the
    # NEXT capture / NEXT race.
    $st = [System.Activator]::CreateInstance($storeType)

    # Build a complete Race in one session (id=15, F1 25 spec): Hamilton + Verstappen,
    # full lap data, full FC.
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 5   # Race @ Monaco
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp ([uint64]500)))))

    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 2
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    $nm0 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($nm0, 0, $pp, 8, $nm0.Length)
    $pp[41] = 1; $pp[44] = 1
    $pp[58] = 0; $pp[61] = 2; $pp[63] = 1
    $nm1 = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
    [System.Array]::Copy($nm1, 0, $pp, 65, $nm1.Length)
    $pp[98] = 1; $pp[101] = 1
    for ($c = 2; $c -lt 22; $c++) {
        $cst = 1 + $c * 57
        $pp[$cst + 3] = 255; $pp[$cst + 5] = 0
    }
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp ([uint64]500)))))

    $ld = New-Object byte[] (22 * 57)
    $ld[33] = 1; $ld[33 + 57] = 1
    $ld[43] = 1; $ld[43 + 57] = 2
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 2 $ld ([uint64]500)))))

    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 2
    $fc[1] = 1; $fc[2] = 70; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]82000).CopyTo($fc, 1 + 7)
    $off2 = 1 + 46
    $fc[$off2 + 0] = 2; $fc[$off2 + 1] = 70; $fc[$off2 + 6] = 3
    [System.BitConverter]::GetBytes([uint32]82500).CopyTo($fc, $off2 + 7)
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc ([uint64]500)))))

    # Phase 1: Finalize and prove the .otk would have captured everything we care about.
    $res1 = $finalizeMethod.Invoke($null, @($st))
    $sessions1 = Get-DictValue $res1 "sessions"
    Assert "v1.1.38 clean contract: pre-clean Finalize emits 1 session" ($sessions1.Count -eq 1)
    $participants1 = Get-DictValue $res1 "participants"
    $hasHam = $false; $hasVer = $false
    foreach ($p in $participants1) {
        if ($p -eq "Hamilton")   { $hasHam = $true }
        if ($p -eq "Verstappen") { $hasVer = $true }
    }
    Assert "v1.1.38 clean contract: pre-clean Finalize keeps Hamilton" $hasHam
    Assert "v1.1.38 clean contract: pre-clean Finalize keeps Verstappen" $hasVer
    $res1Results = Get-DictValue $sessions1[0] "results"
    Assert "v1.1.38 clean contract: pre-clean Finalize has 2 results" ($res1Results.Count -eq 2)

    # Phase 2: BeginNewCapture -- the cleanup half of the pipeline. It must
    # wipe the store completely (the user's "limpar os dados" request).
    $beginMethod = $storeType.GetMethod("BeginNewCapture")
    Assert "v1.1.38 clean contract: SessionStore exposes BeginNewCapture()" ($beginMethod -ne $null)
    if ($beginMethod -ne $null) {
        $beginMethod.Invoke($st, @()) | Out-Null
    }

    $sessDict = Get-Field $st "Sessions"
    Assert "v1.1.38 clean contract: BeginNewCapture empties Sessions dictionary" ($sessDict.Count -eq 0)

    # Phase 3: a fresh Finalize on the cleared store must return zero sessions,
    # zero participants. Critical: if any residue leaks here, the NEXT race's
    # .otk would inherit Hamilton + Verstappen as ghost participants.
    $res2 = $finalizeMethod.Invoke($null, @($st))
    $sessions2 = Get-DictValue $res2 "sessions"
    Assert "v1.1.38 clean contract: post-clean Finalize emits 0 sessions" ($sessions2.Count -eq 0)
    $participants2 = Get-DictValue $res2 "participants"
    Assert "v1.1.38 clean contract: post-clean participants[] is empty (no residue)" ($participants2.Count -eq 0)
}

Write-Host "=== Test 30: BeginNewCapture fully resets store, no residue into next race (v1.1.38 clean contract) ===" -ForegroundColor Cyan
[void](Test-CleanCaptureFullyResetsStoreNoDataLoss)

# Shared helper for v1.1.39 F1 26 tests: build a minimal Race with given
# packetFormat, teamId and trackId. Returns the Finalize result dict.
function Build-F126Race([uint16]$packetFormat, [byte]$teamId, [byte]$trackId, [uint64]$uid) {
    $st = [System.Activator]::CreateInstance($storeType)

    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = $trackId   # Race
    $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp $uid $packetFormat)))) | Out-Null

    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 2
    # ci=0
    $pp[1] = 0; $pp[4] = $teamId; $pp[6] = 44
    $n0 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($n0, 0, $pp, 8, $n0.Length)
    $pp[41] = 1; $pp[44] = 1
    # ci=1
    $pp[58] = 0; $pp[61] = $teamId; $pp[63] = 1
    $n1 = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
    [System.Array]::Copy($n1, 0, $pp, 65, $n1.Length)
    $pp[98] = 1; $pp[101] = 1
    for ($c = 2; $c -lt 22; $c++) { $cst = 1 + $c * 57; $pp[$cst + 3] = 255; $pp[$cst + 5] = 0 }
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp $uid $packetFormat)))) | Out-Null

    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 2
    $fc[1] = 1; $fc[2] = 30; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($fc, 1 + 7)
    $o2 = 1 + 46
    $fc[$o2 + 0] = 2; $fc[$o2 + 1] = 30; $fc[$o2 + 6] = 3
    [System.BitConverter]::GetBytes([uint32]85500).CopyTo($fc, $o2 + 7)
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc $uid $packetFormat)))) | Out-Null

    return $finalizeMethod.Invoke($null, @($st))
}

function Test-F126TeamAndTrackLookups() {
    # v1.1.39 -- teamIds 220-230 and track 42 (Madring) must resolve to names.
    $lt = $lookupsType.GetField("Teams").GetValue($null)
    $expected = @{
        220='Mercedes-AMG F1 Team'; 221='Scuderia Ferrari HP'; 222='Oracle Red Bull Racing';
        223='Atlassian Williams F1 Team'; 224='Aston Martin Aramco'; 225='BWT Alpine F1 Team';
        226='Visa Cash App Racing Bulls'; 227='MoneyGram Haas F1 Team'; 228='McLaren Formula 1 Team';
        229='Audi Revolut F1 Team'; 230='Cadillac Formula 1 Team'
    }
    foreach ($id in $expected.Keys) {
        $name = $null
        $ok = $lt.TryGetValue([int]$id, [ref]$name)
        Assert "v1.1.39: teamId $id present in Lookups.Teams" $ok
        Assert "v1.1.39: teamId $id -> '$($expected[$id])'" ($name -eq $expected[$id])
    }
    $tracks = $lookupsType.GetField("Tracks").GetValue($null)
    $tn = $null
    Assert "v1.1.39: track 42 present" ($tracks.TryGetValue([int]42, [ref]$tn))
    Assert "v1.1.39: track 42 -> Madring" ($tn -eq 'Madring')
}

Write-Host "=== Test 31: F1 26 team (220-230) + track 42 lookups (v1.1.39) ===" -ForegroundColor Cyan
[void](Test-F126TeamAndTrackLookups)

function Test-F126ContentDetectionOn2025Format() {
    # v1.1.39 -- 2026 content (teamId 220) on the 2025 WIRE FORMAT must:
    #   - resolve teamName to the real F1 26 team (parser works in 2025 format)
    #   - flag game=F1_26 (content-aware) even though packetFormat=2025
    #   - NOT set unsupportedUdpFormat (2025 is supported)
    $res = Build-F126Race ([uint16]2025) ([byte]229) ([byte]16) ([uint64]700)
    Assert "v1.1.39: game label is F1_26 for 2026 content on 2025 format" ((Get-DictValue $res "game") -eq "F1_26")
    $dbg = Get-DictValue $res "_debug"
    $g = Get-DictValue $dbg "game"
    Assert "v1.1.39: _debug.game.contentPack2026 = true" ((Get-DictValue $g "contentPack2026") -eq $true)
    Assert "v1.1.39: _debug.game.formatLabel = F1_25 (wire format)" ((Get-DictValue $g "formatLabel") -eq "F1_25")
    Assert "v1.1.39: _debug.game.unsupportedUdpFormat is null on 2025 format" ((Get-DictValue $g "unsupportedUdpFormat") -eq $null)
    # teamName must resolve (Audi = 229)
    $sessions = Get-DictValue $res "sessions"
    $drivers = Get-DictValue $sessions[0] "drivers"
    $ham = Get-DictValue $drivers "Hamilton"
    Assert "v1.1.39: teamId 229 resolves to 'Audi Revolut F1 Team'" ((Get-DictValue $ham "teamName") -eq "Audi Revolut F1 Team")
    # no rawSamples on supported format
    Assert "v1.1.39: no rawSamples on 2025 format" ((Get-DictValue $dbg "rawSamples") -eq $null)
}

Write-Host "=== Test 32: F1 26 content detection on 2025 wire format (v1.1.39) ===" -ForegroundColor Cyan
[void](Test-F126ContentDetectionOn2025Format)

function Test-MadringTrackTriggersContentDetection() {
    # v1.1.39 -- track 42 (Madring) alone (no 220-230 team) flips content to 2026.
    $res = Build-F126Race ([uint16]2025) ([byte]0) ([byte]42) ([uint64]701)
    Assert "v1.1.39: Madring (track 42) flips game to F1_26" ((Get-DictValue $res "game") -eq "F1_26")
    $g = Get-DictValue (Get-DictValue $res "_debug") "game"
    Assert "v1.1.39: Madring sets contentPack2026" ((Get-DictValue $g "contentPack2026") -eq $true)
    $sessions = Get-DictValue $res "sessions"
    $tk = Get-DictValue $sessions[0] "track"
    Assert "v1.1.39: track 42 name = Madring" ((Get-DictValue $tk "name") -eq "Madring")
}

Write-Host "=== Test 33: Madring (track 42) triggers 2026 content detection (v1.1.39) ===" -ForegroundColor Cyan
[void](Test-MadringTrackTriggersContentDetection)

function Test-2026FormatNowSupported() {
    # v1.1.41 -- 2026 is now a SUPPORTED wire format. A 2026 capture must:
    #   - resolve game = F1_26
    #   - NOT flag _debug.game.unsupportedUdpFormat (now null)
    #   - NOT carry _debug.rawSamples (sampler only fires for unsupported formats)
    # (Was Test 34 in v1.1.39, when 2026 was still experimental/sampled.)
    $res = Build-F126Race ([uint16]2026) ([byte]5) ([byte]16) ([uint64]702)
    $dbg = Get-DictValue $res "_debug"
    $g = Get-DictValue $dbg "game"
    Assert "v1.1.41: game = F1_26 for 2026 wire format" ((Get-DictValue $res "game") -eq "F1_26")
    Assert "v1.1.41: unsupportedUdpFormat is NULL (2026 supported)" ((Get-DictValue $g "unsupportedUdpFormat") -eq $null)
    Assert "v1.1.41: no rawSamples for supported 2026" ((Get-DictValue $dbg "rawSamples") -eq $null)
}

Write-Host "=== Test 34: 2026 wire format now supported (no flag, no samples) (v1.1.41) ===" -ForegroundColor Cyan
[void](Test-2026FormatNowSupported)

function Test-PlainF125NoFalsePositives() {
    # v1.1.39 -- a normal F1 25 race (team 5 = Alpine F1 25, track 5 Monaco,
    # 2025 format) must NOT be flagged as 2026 content nor unsupported.
    $res = Build-F126Race ([uint16]2025) ([byte]5) ([byte]5) ([uint64]703)
    Assert "v1.1.39: plain F1 25 stays game=F1_25" ((Get-DictValue $res "game") -eq "F1_25")
    $g = Get-DictValue (Get-DictValue $res "_debug") "game"
    Assert "v1.1.39: plain F1 25 contentPack2026 = false" ((Get-DictValue $g "contentPack2026") -eq $false)
    Assert "v1.1.39: plain F1 25 unsupportedUdpFormat null" ((Get-DictValue $g "unsupportedUdpFormat") -eq $null)
}

Write-Host "=== Test 35: plain F1 25 has no F1 26 false positives (v1.1.39) ===" -ForegroundColor Cyan
[void](Test-PlainF125NoFalsePositives)

function HexToBytes([string]$h) {
    $n = [int]($h.Length / 2)
    $b = New-Object byte[] $n
    for ($i = 0; $i -lt $n; $i++) { $b[$i] = [Convert]::ToByte($h.Substring($i * 2, 2), 16) }
    return ,$b
}

function Test-Participants2026LayoutFromRealCapture() {
    # v1.1.40 -- validates the 2026 Participants parser against REAL bytes from a
    # labeled UDP-Format-2026 capture (Spa_20260604_195534). First 3 AI entries
    # are NORRIS/ALONSO/SAINZ. Confirms teamId@5, name@10, stride 60.
    $partType = $asm.GetType("Overtake.SimHub.Plugin.Packets.ParticipantsData")
    $parse2 = $partType.GetMethod("Parse", [Type[]]@([byte[]], [uint16]))

    $hex = "ea071901140104250f50569780d69400000000000000000000000015ff16013600ffffe40100040a4e4f52524953000000000000000000000000000000000000000000000000000001000000ff03ff80004f4f4f2e2e2effffff010300ffffe001000e4d414c4f4e534f000000000000000000000000000000000000000000000000000001000000ff03229971f9ff84191919ffffff010000ffffdf0100374d5341494e5a00000000000000000000000000000000000000000000000000000001000000ff031868db001dbf1b1b65ffffff010900ffffde010003165645525354415050454e0000000000000000000000000000000000000000000001000000"
    $data = HexToBytes $hex

    # Parse with 2026 layout
    $pd = $parse2.Invoke($null, @([byte[]]$data, [uint16]2026))
    Assert "v1.1.40: 2026 Participants parsed" ($pd -ne $null)
    if ($pd -eq $null) { return }
    $entries = Get-Field $pd "Entries"
    $e0 = $entries[0]; $e1 = $entries[1]; $e2 = $entries[2]
    Assert "v1.1.40: entry0 name = NORRIS"        ((Get-Field $e0 "Name") -eq "NORRIS")
    Assert "v1.1.40: entry0 teamId = 228 (McLaren)" ([int](Get-Field $e0 "TeamId") -eq 228)
    Assert "v1.1.40: entry0 driverId = 54"        ([int](Get-Field $e0 "DriverId") -eq 54)
    Assert "v1.1.40: entry0 raceNumber = 4"       ([int](Get-Field $e0 "RaceNumber") -eq 4)
    Assert "v1.1.40: entry1 name = ALONSO"        ((Get-Field $e1 "Name") -eq "ALONSO")
    Assert "v1.1.40: entry1 teamId = 224 (Aston)" ([int](Get-Field $e1 "TeamId") -eq 224)
    Assert "v1.1.40: entry2 name = SAINZ"         ((Get-Field $e2 "Name") -eq "SAINZ")
    Assert "v1.1.40: entry2 teamId = 223 (Williams)" ([int](Get-Field $e2 "TeamId") -eq 223)

    # Parsing the SAME bytes with the 2025 layout must NOT yield McLaren@0
    # (proves the format actually matters / old parser produced garbage).
    $pd25 = $parse2.Invoke($null, @([byte[]]$data, [uint16]2025))
    $e025 = (Get-Field $pd25 "Entries")[0]
    Assert "v1.1.40: 2025 layout on 2026 bytes does NOT give teamId 228" ([int](Get-Field $e025 "TeamId") -ne 228)
}

Write-Host "=== Test 36: 2026 Participants parser vs real capture (v1.1.40) ===" -ForegroundColor Cyan
[void](Test-Participants2026LayoutFromRealCapture)

function Test-CarStatus2026StrideFromRealCapture() {
    # v1.1.40 -- validates the 2026 CarStatus parser against REAL bytes from the
    # same capture. ERS offsets are unchanged; only the stride is 55->59. Car 0
    # and car 1 both have a full 4 MJ store at the start; reading car 1 with the
    # OLD stride (55) yields garbage (0), proving the stride fix.
    $csType = $asm.GetType("Overtake.SimHub.Plugin.Packets.CarStatusEntry")
    $parse2 = $csType.GetMethod("Parse", [Type[]]@([byte[]], [uint16]))

    $hex = "ea071901140107250f50569780d69400000000000000000000000015ff0001033a006666b6400000dc42d39b5e402b33a00f090000001110000090aac648000000000024744a0300000000000000004054094b00000000000001033a006666b6400000dc42d39b5e402b33a00f09000000111000002629c948000000000024744a0300000000000000004054094b00000000000001033a006666b6400000dc42d39b5e402b33a00f09000000111000004973c448000000000024744a0300000000000000004054094b00000000000001033a006666b6400000dc42d39b5e402b33a00f0900000011100000a5bbc648000000000024744a030000000000000000"
    $data = HexToBytes $hex

    $cs = $parse2.Invoke($null, @([byte[]]$data, [uint16]2026))
    Assert "v1.1.40: 2026 CarStatus parsed" ($cs -ne $null -and $cs.Length -ge 2)
    if ($cs -eq $null -or $cs.Length -lt 2) { return }
    $store0 = [float](Get-Field $cs[0] "ErsStoreEnergy")
    $mode0 = [int](Get-Field $cs[0] "ErsDeployMode")
    $store1 = [float](Get-Field $cs[1] "ErsStoreEnergy")
    Assert "v1.1.40: car0 ERS store = 4 MJ"        ([Math]::Abs($store0 - 4000000) -lt 1)
    Assert "v1.1.40: car0 ERS deployMode = 3"      ($mode0 -eq 3)
    Assert "v1.1.40: car1 ERS store = 4 MJ (stride 59 correct)" ([Math]::Abs($store1 - 4000000) -lt 1)

    # Same bytes with the 2025 stride (55) misaligns car 1 -> not 4 MJ.
    $cs25 = $parse2.Invoke($null, @([byte[]]$data, [uint16]2025))
    $store1_25 = [float](Get-Field $cs25[1] "ErsStoreEnergy")
    Assert "v1.1.40: car1 with WRONG 2025 stride is NOT 4 MJ (proves fix)" ([Math]::Abs($store1_25 - 4000000) -ge 1)
}

Write-Host "=== Test 37: 2026 CarStatus stride parser vs real capture (v1.1.40) ===" -ForegroundColor Cyan
[void](Test-CarStatus2026StrideFromRealCapture)

function Test-LobbyInfo2026FromRealCapture() {
    # v1.1.41 -- validates the 2026 LobbyInfo parser against REAL bytes from a
    # labeled MP lobby capture (Brazil_20260605_140356). entry0 is the user
    # (ERT Drako%, teamId 228, Steam, carNumber 73, names shown); entries 1/2 are
    # players with "Show player names" OFF (-> name "Player"). Confirms stride 43,
    # teamId@1, platform@4, name@5, carNumber@37, yourTelemetry@38, showOnlineNames@39.
    $lobType = $asm.GetType("Overtake.SimHub.Plugin.Packets.LobbyInfoData")
    $parse2 = $lobType.GetMethod("Parse", [Type[]]@([byte[]], [uint16]))

    $hex = "ea071901140109000000000000000010b51244000000000000000002ff0300e4010901455254c2a04472616b6f25000000000000000000000000000000000000000000490101f2000000dd011c03506c6179657200000000000000000000000000000000000000000000000000002a000064000100df011c03506c6179657200000000000000000000000000000000000000000000000000005f00007b0001"
    $data = HexToBytes $hex

    $ld = $parse2.Invoke($null, @([byte[]]$data, [uint16]2026))
    Assert "v1.1.41: 2026 LobbyInfo parsed" ($ld -ne $null)
    if ($ld -eq $null) { return }
    $entries = Get-Field $ld "Entries"
    $e0 = $entries[0]
    Assert "v1.1.41: lobby entry0 name = 'ERT Drako%'"  ((Get-Field $e0 "Name") -like "ERT*Drako%")
    Assert "v1.1.41: lobby entry0 teamId = 228"          ([int](Get-Field $e0 "TeamId") -eq 228)
    Assert "v1.1.41: lobby entry0 platform = 1 (Steam)"  ([int](Get-Field $e0 "Platform") -eq 1)
    Assert "v1.1.41: lobby entry0 carNumber = 73"        ([int](Get-Field $e0 "CarNumber") -eq 73)
    Assert "v1.1.41: lobby entry0 showOnlineNames = 1"   ([int](Get-Field $e0 "ShowOnlineNames") -eq 1)
    $e1 = $entries[1]
    Assert "v1.1.41: lobby entry1 teamId = 221 (Ferrari)" ([int](Get-Field $e1 "TeamId") -eq 221)
    Assert "v1.1.41: lobby entry1 showOnlineNames = 0 (hidden)" ([int](Get-Field $e1 "ShowOnlineNames") -eq 0)

    # 2025 layout on the same bytes must NOT yield the correct name at offset 5.
    $ld25 = $parse2.Invoke($null, @([byte[]]$data, [uint16]2025))
    $e025 = (Get-Field $ld25 "Entries")[0]
    Assert "v1.1.41: 2025 layout misreads carNumber (proves format matters)" ([int](Get-Field $e025 "CarNumber") -ne 73)
}

Write-Host "=== Test 38: 2026 LobbyInfo parser vs real capture (v1.1.41) ===" -ForegroundColor Cyan
[void](Test-LobbyInfo2026FromRealCapture)

function Test-LobbySettingsOmittedFor2026() {
    # v1.1.41 -- the deep Session lobby-settings block is NOT reliably mapped for
    # the 2026 wire format, so it must be OMITTED (null) rather than emit garbage.
    # Build a 2026 race whose Session packet carries non-zero deep-field bytes;
    # the finalizer must still omit lobbySettings. For 2025 the same data yields a
    # populated lobbySettings (control).
    function Build-RaceWithLobbySettings([uint16]$fmt, [uint64]$uid) {
        $st = [System.Activator]::CreateInstance($storeType)
        $sp = New-Object byte[] 753
        $sp[0] = 1; $sp[6] = 15; $sp[7] = 5
        $sp[124] = 0; $sp[125] = 1
        # Deep lobby-settings bytes (F1 25 offsets, payload-relative -> +29).
        $sp[666] = 1   # ruleSet = Race (non-zero -> hasData)
        $sp[649] = 1   # collisions-ish non-zero
        $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp $uid $fmt)))) | Out-Null
        $pp = New-Object byte[] 1256
        for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
        $pp[0] = 1; $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
        $nm = [System.Text.Encoding]::UTF8.GetBytes("Hamilton"); [System.Array]::Copy($nm, 0, $pp, 8, $nm.Length)
        $pp[41] = 1; $pp[44] = 1
        for ($c = 1; $c -lt 22; $c++) { $cst = 1 + $c * 57; $pp[$cst + 3] = 255; $pp[$cst + 5] = 0 }
        $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp $uid $fmt)))) | Out-Null
        $fc = New-Object byte[] (1 + 22 * 46); $fc[0] = 1; $fc[1] = 1; $fc[2] = 5; $fc[6] = 3
        [System.BitConverter]::GetBytes([uint32]85000).CopyTo($fc, 1 + 7)
        $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc $uid $fmt)))) | Out-Null
        return $finalizeMethod.Invoke($null, @($st))
    }

    $res25 = Build-RaceWithLobbySettings ([uint16]2025) ([uint64]810)
    $ls25 = Get-DictValue (Get-DictValue $res25 "sessions")[0] "lobbySettings"
    Assert "v1.1.41: 2025 lobbySettings IS populated (control)" ($ls25 -ne $null)

    $res26 = Build-RaceWithLobbySettings ([uint16]2026) ([uint64]811)
    $ls26 = Get-DictValue (Get-DictValue $res26 "sessions")[0] "lobbySettings"
    Assert "v1.1.41: 2026 lobbySettings is OMITTED (null, not garbage)" ($ls26 -eq $null)
}

Write-Host "=== Test 39: lobbySettings omitted for 2026 (deep fields unmapped) (v1.1.41) ===" -ForegroundColor Cyan
[void](Test-LobbySettingsOmittedFor2026)

function Test-ErsRegulationLimits() {
    # v1.1.42 -- GameInfo ERS regulation ceilings (FIA 2026 PU regs).
    $gi = $asm.GetType("Overtake.SimHub.Plugin.Packets.GameInfo")
    $deploy = $gi.GetMethod("ErsDeployLimitMjPerLap")
    $harvest = $gi.GetMethod("ErsHarvestMgukLimitMjPerLap")
    Assert "v1.1.42: deploy limit 2025 = 4 MJ"  ([double]$deploy.Invoke($null, @([uint16]2025)) -eq 4.0)
    Assert "v1.1.42: deploy limit 2026 = 9 MJ"  ([double]$deploy.Invoke($null, @([uint16]2026)) -eq 9.0)
    Assert "v1.1.42: harvest MGU-K limit 2025 = 2 MJ" ([double]$harvest.Invoke($null, @([uint16]2025)) -eq 2.0)
    Assert "v1.1.42: harvest MGU-K limit 2026 = 8.5 MJ" ([double]$harvest.Invoke($null, @([uint16]2026)) -eq 8.5)
}

Write-Host "=== Test 40: ERS regulation limits (FIA 2026) (v1.1.42) ===" -ForegroundColor Cyan
[void](Test-ErsRegulationLimits)

function Test-ErsRecalibration2026() {
    # v1.1.42 -- end-to-end ERS recalibration. Build a 2026 race where car 0
    # deploys 7 MJ and harvests 5 MJ (MGU-K) in lap 1, then the counter resets
    # (lap rollover). Expected: deployedMjAvgPerLap ~= 7.0, deployedPctAvgPerLap
    # ~= 7/9*100 = 77.78; harvestedMgukMjAvgPerLap ~= 5.0, pct ~= 5/8.5*100 = 58.82.
    $st = [System.Activator]::CreateInstance($storeType)
    $fmt = [uint16]2026

    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 5; $sp[124] = 0; $sp[125] = 1
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp ([uint64]900) $fmt)))) | Out-Null

    # Participants 2026 (1 human, carIdx 0). 2026 layout: teamId@5, name@10.
    $pp = New-Object byte[] 1500
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 1
    $pp[1] = 0           # ai=false
    $pp[1+5] = 220       # teamId@5 (Mercedes 2026)
    $pp[1+8] = 44        # raceNumber@8
    $nm = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($nm, 0, $pp, 1+10, $nm.Length)   # name@10
    $pp[1+43] = 1        # showOnlineNames@43
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp ([uint64]900) $fmt)))) | Out-Null

    # CarStatus 2026 builder: stride 59, 1 entry. store@37, deployMode@41,
    # harvMguk@42, deployed@50 (all floats in Joules).
    function New-CarStatus2026([uint32]$storeJ, [uint32]$harvMgukJ, [uint32]$deployedJ) {
        $body = New-Object byte[] 59
        $body[0] = 0; $body[1] = 1; $body[2] = 3                       # TC/ABS/fuelMix
        [System.BitConverter]::GetBytes([float]5.0).CopyTo($body, 5)   # fuelInTank
        [System.BitConverter]::GetBytes([float]110.0).CopyTo($body, 9) # fuelCap (>= min)
        [System.BitConverter]::GetBytes([float]3.0).CopyTo($body, 13)  # fuelRemLaps
        [System.BitConverter]::GetBytes([float]$storeJ).CopyTo($body, 37)
        $body[41] = 2                                                  # deployMode
        [System.BitConverter]::GetBytes([float]$harvMgukJ).CopyTo($body, 42)
        [System.BitConverter]::GetBytes([float]$deployedJ).CopyTo($body, 50)
        return ,$body
    }
    # Lap 1: deployed 7 MJ, harvested MGU-K 5 MJ, store full.
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 7 (New-CarStatus2026 4000000 5000000 7000000) ([uint64]900) $fmt)))) | Out-Null
    # Lap rollover: counters reset to 0 -> pushes lap-1 values to per-lap arrays.
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 7 (New-CarStatus2026 4000000 0 0) ([uint64]900) $fmt)))) | Out-Null

    $fc = New-Object byte[] (1 + 22 * 46); $fc[0] = 1; $fc[1] = 1; $fc[2] = 5; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($fc, 1 + 7)
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc ([uint64]900) $fmt)))) | Out-Null

    $res = $finalizeMethod.Invoke($null, @($st))
    $drivers = Get-DictValue (Get-DictValue $res "sessions")[0] "drivers"
    $ham = Get-DictValue $drivers "Hamilton"
    Assert "v1.1.42: Hamilton present" ($ham -ne $null)
    if ($ham -eq $null) { return }
    $ers = Get-DictValue $ham "ersTelemetry"
    Assert "v1.1.42: ersTelemetry present" ($ers -ne $null)
    if ($ers -eq $null) { return }
    $depMj = [double](Get-DictValue $ers "deployedMjAvgPerLap")
    $depPct = [double](Get-DictValue $ers "deployedPctAvgPerLap")
    $harvMj = [double](Get-DictValue $ers "harvestedMgukMjAvgPerLap")
    $harvPct = [double](Get-DictValue $ers "harvestedMgukPctAvgPerLap")
    Assert "v1.1.42: deployedMjAvgPerLap ~= 7.0" ([Math]::Abs($depMj - 7.0) -lt 0.05)
    Assert "v1.1.42: deployedPctAvgPerLap ~= 77.8 (of 9 MJ, bounded <100)" ([Math]::Abs($depPct - 77.78) -lt 0.5)
    Assert "v1.1.42: harvestedMgukMjAvgPerLap ~= 5.0" ([Math]::Abs($harvMj - 5.0) -lt 0.05)
    Assert "v1.1.42: harvestedMgukPctAvgPerLap ~= 58.8 (of 8.5 MJ)" ([Math]::Abs($harvPct - 58.82) -lt 0.5)
    Assert "v1.1.42: deployLimitMjPerLap = 9" ([double](Get-DictValue $ers "deployLimitMjPerLap") -eq 9.0)
    Assert "v1.1.42: storePctAvg still 0..100" (([double](Get-DictValue $ers "storePctAvg") -ge 0) -and ([double](Get-DictValue $ers "storePctAvg") -le 100))
}

Write-Host "=== Test 41: ERS recalibration end-to-end (2026 deploy 7MJ -> 77.8%) (v1.1.42) ===" -ForegroundColor Cyan
[void](Test-ErsRecalibration2026)

function Test-SessionDeepProbe2026() {
    # v1.1.43 -- a 2026 Session packet must produce _debug.sessionDeepProbe (the
    # diagnostic that captures the LATEST Session packet so the deep lobby-settings
    # offsets can be reverse-engineered). A 2025 capture must NOT (deep fields are
    # already mapped). The probe must NOT re-flag the file as unsupported.
    $res26 = Build-F126Race ([uint16]2026) ([byte]221) ([byte]42) ([uint64]920)
    $dbg26 = Get-DictValue $res26 "_debug"
    $probe = Get-DictValue $dbg26 "sessionDeepProbe"
    Assert "v1.1.43: 2026 produces _debug.sessionDeepProbe" ($probe -ne $null)
    if ($probe -ne $null) {
        Assert "v1.1.43: probe carries packetFormat 2026" ([int](Get-DictValue $probe "packetFormat") -eq 2026)
        $hx = Get-DictValue $probe "hexPrefix"
        Assert "v1.1.43: probe hex non-empty + starts ea07 (2026)" (($hx -ne $null) -and $hx.StartsWith("ea07"))
    }
    # Must NOT re-flag the file (2026 stays supported).
    $g26 = Get-DictValue $dbg26 "game"
    Assert "v1.1.43: probe does not set unsupportedUdpFormat" ((Get-DictValue $g26 "unsupportedUdpFormat") -eq $null)

    $res25 = Build-F126Race ([uint16]2025) ([byte]5) ([byte]5) ([uint64]921)
    $probe25 = Get-DictValue (Get-DictValue $res25 "_debug") "sessionDeepProbe"
    Assert "v1.1.43: 2025 does NOT produce sessionDeepProbe (deep fields mapped)" ($probe25 -eq $null)
}

Write-Host "=== Test 42: Session deep-field probe for 2026 (v1.1.43 diagnostic) ===" -ForegroundColor Cyan
[void](Test-SessionDeepProbe2026)

function Test-UpdateAdvisorSeverity() {
    # v1.1.44 -- the update advisory must escalate severity so an out-of-date user
    # (the v1.1.27 -> F1 26 garbage-export case) gets a LOUD warning, not the old
    # passive yellow banner. Pure logic, loaded via reflection.
    $advisorType = $asm.GetType("Overtake.SimHub.Plugin.UpdateAdvisor")
    Assert "v1.1.44: UpdateAdvisor type exists" ($advisorType -ne $null)
    if ($advisorType -eq $null) { return }
    $eval = $advisorType.GetMethod("Evaluate")
    $tok = $advisorType.GetMethod("StatusToken")
    Assert "v1.1.44: Evaluate method exists" ($eval -ne $null)
    Assert "v1.1.44: StatusToken method exists" ($tok -ne $null)
    if ($eval -eq $null -or $tok -eq $null) { return }

    function Sev([string]$cur, [string]$latest, [string]$min, [int]$fmt) {
        $sev = $eval.Invoke($null, [object[]]@($cur, $latest, $min, [int]$fmt))
        return $tok.Invoke($null, [object[]]@($sev))
    }

    Assert "v1.1.44: current==latest -> UpToDate" ((Sev "1.1.43" "1.1.43" "1.1.41" 0) -eq "UpToDate")
    Assert "v1.1.44: current newer than latest -> UpToDate" ((Sev "1.1.44" "1.1.43" "1.1.41" 0) -eq "UpToDate")
    Assert "v1.1.44: behind latest, above min -> UpdateAvailable" ((Sev "1.1.42" "1.1.43" "1.1.41" 0) -eq "UpdateAvailable")
    Assert "v1.1.44: exactly at min -> UpdateAvailable (not Required)" ((Sev "1.1.41" "1.1.43" "1.1.41" 0) -eq "UpdateAvailable")
    Assert "v1.1.44: below min (1.1.27 case) -> UpdateRequired" ((Sev "1.1.27" "1.1.43" "1.1.41" 0) -eq "UpdateRequired")
    Assert "v1.1.44: below min even if latest check failed -> UpdateRequired" ((Sev "1.1.27" "" "1.1.41" 0) -eq "UpdateRequired")
    Assert "v1.1.44: live unsupported format outranks everything -> UnsupportedFormat" ((Sev "1.1.43" "1.1.43" "1.1.41" 2027) -eq "UnsupportedFormat")
    Assert "v1.1.44: unsupported format on old build too -> UnsupportedFormat" ((Sev "1.1.27" "1.1.43" "1.1.41" 2027) -eq "UnsupportedFormat")
    Assert "v1.1.44: 4-part current version parses -> UpdateRequired" ((Sev "1.1.27.0" "1.1.43" "1.1.41" 0) -eq "UpdateRequired")
    Assert "v1.1.44: v-prefixed versions parse" ((Sev "v1.1.42" "v1.1.43" "v1.1.41" 0) -eq "UpdateAvailable")
    Assert "v1.1.44: garbage current version is safe (no false alarm) -> UpToDate" ((Sev "not-a-version" "1.1.43" "1.1.41" 0) -eq "UpToDate")
    Assert "v1.1.44: missing min field -> UpdateAvailable (no spurious Required)" ((Sev "1.1.27" "1.1.43" "" 0) -eq "UpdateAvailable")
}

Write-Host "=== Test 43: Update advisory severity escalation (v1.1.44) ===" -ForegroundColor Cyan
[void](Test-UpdateAdvisorSeverity)

function Test-F126SprintRaceId15NotTerminal() {
    # v1.1.45 -- F1 26 reports Sprint Race as sessionType id=15 ("Race"), same as
    # Main Race. Without sprint-context deferral, auto-export fired at Sprint end
    # (Austria_20260622_215918_77AEAC.otk regression).
    $st = [System.Activator]::CreateInstance($storeType)
    $sprintHelper = $asm.GetType("Overtake.SimHub.Plugin.Finalizer.SprintFormatHelper")
    $isClosing = $sprintHelper.GetMethod("IsTerminalRaceClosing")
    $getExportId = $sprintHelper.GetMethod("GetExportSessionTypeId")
    Assert "v1.1.45: SprintFormatHelper type exists" ($sprintHelper -ne $null)
    if ($sprintHelper -eq $null) { return }

    $feed = {
        param([uint64]$uid, [byte]$stype)
        $sp = New-Object byte[] 700
        $sp[0] = 1; $sp[6] = $stype; $sp[7] = 14
        $sp[124] = 0; $sp[125] = 1
        $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp $uid))))
    }
    $feedFc = {
        param([uint64]$uid)
        $fc = New-Object byte[] (1 + 22 * 46)
        $fc[0] = 1; $fc[1] = 1; $fc[2] = 5; $fc[6] = 3
        [System.BitConverter]::GetBytes([uint32]88000).CopyTo($fc, 1 + 7)
        $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc $uid))))
    }

    # Sprint Shootout + Sprint Race (wire id=15) before any Qualifying.
    & $feed ([uint64]200) ([byte]10)
    & $feedFc ([uint64]200)
    & $feed ([uint64]201) ([byte]15)
    & $feedFc ([uint64]201)

    $closing = $isClosing.Invoke($null, [object[]]@([byte]15, $st))
    Assert "v1.1.45: id=15 Sprint Race before Quali is NOT terminal closing" (-not $closing)
    Assert "v1.1.45: HasClosedTerminalSession false before Main Quali" (-not $st.HasClosedTerminalSession())

    # Main Qualifying arrives -- still not terminal (Quali itself is not Race).
    & $feed ([uint64]202) ([byte]5)
    & $feedFc ([uint64]202)
    Assert "v1.1.45: HasClosedTerminalSession still false after Quali only" (-not $st.HasClosedTerminalSession())

    # Main Race (second wire id=15) after Quali -- now terminal.
    & $feed ([uint64]203) ([byte]15)
    & $feedFc ([uint64]203)
    Assert "v1.1.45: HasClosedTerminalSession true after Main Race post-Quali" $st.HasClosedTerminalSession()

    $res = $finalizeMethod.Invoke($null, @($st))
    $sessions = Get-DictValue $res "sessions"
    Assert "v1.1.45: sprint weekend emits 4 sessions (SS+Sprint+Quali+Race)" ($sessions.Count -eq 4)

    # First wire id=15 session (uid 201) exports as Race2 (Sprint Race).
    $sprintSess = $null
    foreach ($s in $sessions) {
        if ((Get-DictValue $s "sessionUID").ToString() -eq "201") { $sprintSess = $s; break }
    }
    Assert "v1.1.45: sprint race session found" ($sprintSess -ne $null)
    if ($sprintSess -eq $null) { return }
    $stObj = Get-DictValue $sprintSess "sessionType"
    $stName = (Get-DictValue $stObj "name").ToString()
    Assert "v1.1.45: first wire-id=15 exports as Race2 (Sprint Race)" ($stName -eq "Race2")
}

Write-Host "=== Test 44: F1 26 Sprint Race as id=15 deferred until Quali (v1.1.45) ===" -ForegroundColor Cyan
[void](Test-F126SprintRaceId15NotTerminal)

function Test-ExportJsonNoNanToken() {
    # v1.1.45 -- non-finite ERS/fuel values must serialize as null, never the
    # literal JSON token NaN (breaks JSON.parse on the import server).
    $st = [System.Activator]::CreateInstance($storeType)
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 5
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp))))

    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 1
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    $n0 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
    [System.Array]::Copy($n0, 0, $pp, 8, $n0.Length)
    $pp[41] = 1; $pp[44] = 1
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp))))

    # One CarStatus packet marks ErsCaptured; we poison per-lap arrays with NaN.
    $cs = New-Object byte[] (2 * 55)
    for ($zi = 0; $zi -lt $cs.Length; $zi++) { $cs[$zi] = 0 }
    [System.BitConverter]::GetBytes([float]4000000).CopyTo($cs, 29)
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 7 $cs))))

    $sessRun = $st.Sessions.Values | Select-Object -First 1
    $driver = $sessRun.Drivers.Values | Select-Object -First 1
    $driver.ErsCaptured = $true
    $driver.DeployedPctPerLap.Add([float]::NaN)
    $driver.DeployedPctPerLap.Add([float]::NaN)
    $driver.FuelCaptured = $true
    $driver.FuelCapacityKg = [float]::NaN

    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 1; $fc[1] = 1; $fc[2] = 5; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($fc, 1 + 7)
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc))))

    $res = $finalizeMethod.Invoke($null, @($st))
    $exportNums = $asm.GetType("Overtake.SimHub.Plugin.Finalizer.ExportNumbers")
    $sanitize = $exportNums.GetMethod("SanitizeForJson")
    $sanitize.Invoke($null, @($res)) | Out-Null

    [void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = [int]::MaxValue
    $json = $ser.Serialize($res)

    Assert "v1.1.45: serialized JSON contains no literal NaN token" (-not ($json -match '(^|[^A-Za-z])NaN([^A-Za-z]|$)'))
    $sess0 = (Get-DictValue $res "sessions")[0]
    $drivers = Get-DictValue $sess0 "drivers"
    $ham = Get-DictValue $drivers "Hamilton"
    if ($ham -eq $null) { $ham = $drivers.Values | Select-Object -First 1 }
    $ers = Get-DictValue $ham "ersTelemetry"
    $deployedArr = Get-DictValue $ers "deployedPctPerLap"
    $allNullOrFinite = $true
    foreach ($x in $deployedArr) {
        if ($x -ne $null -and [double]::IsNaN([double]$x)) { $allNullOrFinite = $false; break }
    }
    Assert "v1.1.45: deployedPctPerLap has no NaN elements" $allNullOrFinite
    Assert "v1.1.45: fuelCapacityKg is null" ((Get-DictValue (Get-DictValue $ham "fuelTelemetry") "fuelCapacityKg") -eq $null)
}

Write-Host "=== Test 45: ERS/fuel NaN -> null in JSON export (v1.1.45) ===" -ForegroundColor Cyan
[void](Test-ExportJsonNoNanToken)

function Test-PlayerNameControlCharStrip() {
    # v1.1.45 -- strip ASCII control chars from UDP name fields (e.g. leading TAB).
    $pktStrings = $asm.GetType("Overtake.SimHub.Plugin.Packets.PacketStrings")
    $sanitize = $pktStrings.GetMethod("SanitizePlayerName")
    $clean = $sanitize.Invoke($null, @("`tPRT_martbryt"))
    Assert "v1.1.45: SanitizePlayerName strips leading TAB" ($clean -eq "PRT_martbryt")

    $st = [System.Activator]::CreateInstance($storeType)
    $sp = New-Object byte[] 700
    $sp[0] = 1; $sp[6] = 15; $sp[7] = 5
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 1 $sp))))

    $pp = New-Object byte[] 1256
    for ($zi = 0; $zi -lt $pp.Length; $zi++) { $pp[$zi] = 0 }
    $pp[0] = 1
    $pp[1] = 0; $pp[4] = 0; $pp[6] = 44
    $n0 = [System.Text.Encoding]::UTF8.GetBytes("`tPRT_martbryt")
    [System.Array]::Copy($n0, 0, $pp, 8, $n0.Length)
    $pp[41] = 1; $pp[44] = 1
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 4 $pp))))

    $fc = New-Object byte[] (1 + 22 * 46)
    $fc[0] = 1; $fc[1] = 1; $fc[2] = 5; $fc[6] = 3
    [System.BitConverter]::GetBytes([uint32]85000).CopyTo($fc, 1 + 7)
    $ingestMethod.Invoke($st, @((Dispatch (New-FakePacket 8 $fc))))

    $res = $finalizeMethod.Invoke($null, @($st))
    $drivers = Get-DictValue (Get-DictValue $res "sessions")[0] "drivers"
    Assert "v1.1.45: driver key has no control chars" ($drivers.ContainsKey("PRT_martbryt"))
    $dbg = Get-DictValue $res "_debug"
    $lobby = Get-DictValue (Get-DictValue $dbg "diagnostics") "lobbyInfo"
    $bkt = Get-DictValue $lobby "bestKnownTags"
    $hasClean = $false
    foreach ($kv in $bkt.GetEnumerator()) {
        if ($kv.Value -eq "PRT_martbryt") { $hasClean = $true; break }
    }
    Assert "v1.1.45: bestKnownTags stores sanitized name" $hasClean
}

Write-Host "=== Test 46: Player name control-char sanitization (v1.1.45) ===" -ForegroundColor Cyan
[void](Test-PlayerNameControlCharStrip)

# ---- Summary ----
Write-Host ""
Write-Host "======================================" -ForegroundColor Yellow
$color = "Green"
if ($fail -gt 0) { $color = "Red" }
Write-Host "  PASS: $pass   FAIL: $fail" -ForegroundColor $color
Write-Host "======================================" -ForegroundColor Yellow
Write-Host ""
exit $fail
