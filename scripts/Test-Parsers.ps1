# Test-Parsers.ps1 — Validates C# packet parsers against known binary data
# Loads the built DLL, constructs fake packets, and verifies parsed output.

param(
    [string]$DllPath = "$PSScriptRoot\..\src\Overtake.SimHub.Plugin\bin\Release\Overtake.SimHub.Plugin.dll"
)

$ErrorActionPreference = "Stop"
$pass = 0
$fail = 0

function Assert-Equal($name, $expected, $actual) {
    if ($expected -ne $actual) {
        Write-Host "  [FAIL] $name : expected=$expected actual=$actual" -ForegroundColor Red
        $script:fail++
    } else {
        Write-Host "  [PASS] $name = $actual" -ForegroundColor Green
        $script:pass++
    }
}

# Load assembly
$asm = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $DllPath))

# Invoke a static method(byte[]) via reflection, handling PowerShell's PSObject wrapping
function Invoke-Parse($type, [byte[]]$buf, [string]$methodName = "Parse") {
    $method = $type.GetMethod($methodName)
    return $method.Invoke($null, [object[]]@(,[byte[]]$buf))
}

# Helper: write little-endian values into a byte array
function Write-UInt16([byte[]]$buf, [int]$off, [uint16]$val) {
    $bytes = [BitConverter]::GetBytes($val)
    [Array]::Copy($bytes, 0, $buf, $off, 2)
}
function Write-UInt32([byte[]]$buf, [int]$off, [uint32]$val) {
    $bytes = [BitConverter]::GetBytes($val)
    [Array]::Copy($bytes, 0, $buf, $off, 4)
}
function Write-UInt64([byte[]]$buf, [int]$off, [uint64]$val) {
    $bytes = [BitConverter]::GetBytes($val)
    [Array]::Copy($bytes, 0, $buf, $off, 8)
}
function Write-Float([byte[]]$buf, [int]$off, [float]$val) {
    $bytes = [BitConverter]::GetBytes($val)
    [Array]::Copy($bytes, 0, $buf, $off, 4)
}
function Write-Double([byte[]]$buf, [int]$off, [double]$val) {
    $bytes = [BitConverter]::GetBytes($val)
    [Array]::Copy($bytes, 0, $buf, $off, 8)
}

function Make-Header([byte[]]$buf, [byte]$packetId, [uint64]$sessionUid) {
    Write-UInt16 $buf 0 ([uint16]2025)   # packetFormat
    $buf[2] = 25                          # gameYear
    $buf[3] = 1                           # gameMajorVersion
    $buf[4] = 0                           # gameMinorVersion
    $buf[5] = 1                           # packetVersion
    $buf[6] = $packetId
    Write-UInt64 $buf 7 $sessionUid
    Write-Float $buf 15 ([float]12.5)     # sessionTime
    Write-UInt32 $buf 19 ([uint32]100)    # frameIdentifier
    Write-UInt32 $buf 23 ([uint32]200)    # overallFrameIdentifier
    $buf[27] = 0                          # playerCarIndex
    $buf[28] = 255                        # secondaryPlayerCarIndex
}

# ── Test 1: PacketHeader ──
Write-Host "`n=== Test: PacketHeader ===" -ForegroundColor Cyan
$headerBuf = New-Object byte[] 29
Make-Header $headerBuf 1 ([uint64]1234567890123)

$headerType = $asm.GetType("Overtake.SimHub.Plugin.Packets.PacketHeader")
$header = Invoke-Parse $headerType $headerBuf

Assert-Equal "PacketFormat" 2025 $header.PacketFormat
Assert-Equal "GameYear" 25 $header.GameYear
Assert-Equal "PacketId" 1 $header.PacketId
Assert-Equal "SessionUid" ([uint64]1234567890123) $header.SessionUid
Assert-Equal "PlayerCarIndex" 0 $header.PlayerCarIndex
Assert-Equal "SecondaryPlayerCarIndex" 255 $header.SecondaryPlayerCarIndex

# ── Test 2: SessionData (Packet 1) ──
Write-Host "`n=== Test: SessionData (Packet 1) ===" -ForegroundColor Cyan
$sessionBuf = New-Object byte[] 800
Make-Header $sessionBuf 1 ([uint64]111)

$p = 29
$sessionBuf[$p + 0] = 1          # weather = 1 (light rain)
$sessionBuf[$p + 1] = [byte]28   # trackTemp = 28 (signed)
$sessionBuf[$p + 2] = [byte]22   # airTemp = 22
$sessionBuf[$p + 3] = 58         # totalLaps
Write-UInt16 $sessionBuf ($p + 4) ([uint16]5303) # trackLength
$sessionBuf[$p + 6] = 10         # sessionType = 10 (Race)
$sessionBuf[$p + 7] = 14         # trackId = 14
$sessionBuf[$p + 8] = 0          # formula = 0 (F1 Modern)
$sessionBuf[$p + 124] = 1        # safetyCarStatus = 1 (Full SC)
$sessionBuf[$p + 125] = 1        # networkGame = 1 (online)
$sessionBuf[$p + 126] = 1        # numWeatherForecastSamples = 1
$sessionBuf[$p + 127] = 10       # forecast[0].sessionType = 10
$sessionBuf[$p + 128] = 5        # forecast[0].timeOffsetMin = 5
$sessionBuf[$p + 129] = 2        # forecast[0].weather = 2
$sessionBuf[$p + 676] = 2        # numSafetyCarPeriods
$sessionBuf[$p + 677] = 1        # numVSCPeriods
$sessionBuf[$p + 678] = 0        # numRedFlagPeriods

$sessionType = $asm.GetType("Overtake.SimHub.Plugin.Packets.SessionData")
$session = Invoke-Parse $sessionType $sessionBuf

Assert-Equal "Weather" 1 $session.Weather
Assert-Equal "TrackTempC" 28 $session.TrackTempC
Assert-Equal "AirTempC" 22 $session.AirTempC
Assert-Equal "TotalLaps" 58 $session.TotalLaps
Assert-Equal "SessionType" 10 $session.SessionType
Assert-Equal "TrackId" 14 $session.TrackId
Assert-Equal "SafetyCarStatus" 1 $session.SafetyCarStatus
Assert-Equal "NumWeatherForecast" 1 $session.NumWeatherForecastSamples
Assert-Equal "Forecast[0].Weather" 2 $session.WeatherForecast[0].Weather
Assert-Equal "NumSafetyCarPeriods" 2 $session.NumSafetyCarPeriods
Assert-Equal "NumVSCPeriods" 1 $session.NumVirtualSafetyCarPeriods

# ── Test 3: LapData (Packet 2) ──
Write-Host "`n=== Test: LapDataEntry (Packet 2) ===" -ForegroundColor Cyan
$lapBuf = New-Object byte[] (29 + 57 * 22)
Make-Header $lapBuf 2 ([uint64]222)

$car0Off = 29
Write-UInt32 $lapBuf ($car0Off + 0) ([uint32]91234)   # lastLapTimeInMS
Write-UInt32 $lapBuf ($car0Off + 4) ([uint32]45000)    # currentLapTimeInMS
Write-UInt16 $lapBuf ($car0Off + 8) ([uint16]30500)    # sector1 msPart
$lapBuf[$car0Off + 10] = 0                              # sector1 minPart (0*60000 + 30500 = 30500)
Write-UInt16 $lapBuf ($car0Off + 11) ([uint16]1234)    # sector2 msPart
$lapBuf[$car0Off + 13] = 1                              # sector2 minPart (1*60000 + 1234 = 61234)
$lapBuf[$car0Off + 32] = 1                              # carPosition
$lapBuf[$car0Off + 33] = 5                              # currentLapNum
$lapBuf[$car0Off + 34] = 0                              # pitStatus = none
$lapBuf[$car0Off + 35] = 2                              # numPitStops
$lapBuf[$car0Off + 38] = 5                              # penalties (5s)
$lapBuf[$car0Off + 45] = 3                              # resultStatus

$lapType = $asm.GetType("Overtake.SimHub.Plugin.Packets.LapDataEntry")
$laps = Invoke-Parse $lapType $lapBuf

Assert-Equal "Car0.LastLapTimeInMS" ([uint32]91234) $laps[0].LastLapTimeInMS
Assert-Equal "Car0.Sector1TimeInMS" 30500 $laps[0].Sector1TimeInMS
Assert-Equal "Car0.Sector2TimeInMS" 61234 $laps[0].Sector2TimeInMS
Assert-Equal "Car0.CarPosition" 1 $laps[0].CarPosition
Assert-Equal "Car0.CurrentLapNum" 5 $laps[0].CurrentLapNum
Assert-Equal "Car0.NumPitStops" 2 $laps[0].NumPitStops
Assert-Equal "Car0.Penalties" 5 $laps[0].Penalties
Assert-Equal "Car0.ResultStatus" 3 $laps[0].ResultStatus

# ── Test 4: EventData (Packet 3) ──
Write-Host "`n=== Test: EventData (Packet 3) ===" -ForegroundColor Cyan

# Test OVTK event
$evtBuf = New-Object byte[] 40
Make-Header $evtBuf 3 ([uint64]333)
[System.Text.Encoding]::ASCII.GetBytes("OVTK").CopyTo($evtBuf, 29)
$evtBuf[33] = 6   # overtakerIdx
$evtBuf[34] = 1   # overtakenIdx

$evtType = $asm.GetType("Overtake.SimHub.Plugin.Packets.EventData")
$evt = Invoke-Parse $evtType $evtBuf

Assert-Equal "Event.Code" "OVTK" $evt.Code
Assert-Equal "Event.OvertakerIdx" 6 $evt.OvertakerIdx
Assert-Equal "Event.OvertakenIdx" 1 $evt.OvertakenIdx

# Test FTLP event
$ftlpBuf = New-Object byte[] 40
Make-Header $ftlpBuf 3 ([uint64]333)
[System.Text.Encoding]::ASCII.GetBytes("FTLP").CopyTo($ftlpBuf, 29)
$ftlpBuf[33] = 3   # vehicleIdx
Write-Float $ftlpBuf 34 ([float]78.123)

$ftlp = Invoke-Parse $evtType $ftlpBuf
Assert-Equal "FTLP.Code" "FTLP" $ftlp.Code
Assert-Equal "FTLP.VehicleIdx" 3 $ftlp.FastestLapVehicleIdx
# Float comparison with tolerance
$ftlpDiff = [Math]::Abs($ftlp.FastestLapTimeSec - 78.123)
if ($ftlpDiff -lt 0.01) {
    Write-Host "  [PASS] FTLP.LapTimeSec ~= 78.123" -ForegroundColor Green; $pass++
} else {
    Write-Host "  [FAIL] FTLP.LapTimeSec = $($ftlp.FastestLapTimeSec)" -ForegroundColor Red; $fail++
}

# Test BUTN is filtered
$btnBuf = New-Object byte[] 40
Make-Header $btnBuf 3 ([uint64]333)
[System.Text.Encoding]::ASCII.GetBytes("BUTN").CopyTo($btnBuf, 29)
$btnResult = Invoke-Parse $evtType $btnBuf
if ($null -eq $btnResult) {
    Write-Host "  [PASS] BUTN filtered (null)" -ForegroundColor Green; $pass++
} else {
    Write-Host "  [FAIL] BUTN should be null" -ForegroundColor Red; $fail++
}

# ── Test 5: ParticipantsData (Packet 4) ──
Write-Host "`n=== Test: ParticipantsData (Packet 4) ===" -ForegroundColor Cyan
$partBuf = New-Object byte[] (29 + 1 + 57 * 2)
Make-Header $partBuf 4 ([uint64]444)
$partBuf[29] = 2   # numActiveCars

# Car 0: name = "Verstappen"
$car0Base = 30
$partBuf[$car0Base + 0] = 0    # aiControlled = false
$partBuf[$car0Base + 3] = 1    # teamId = 1 (Red Bull)
$partBuf[$car0Base + 5] = 1    # raceNumber = 1
$nameBytes = [System.Text.Encoding]::UTF8.GetBytes("Verstappen")
[Array]::Copy($nameBytes, 0, $partBuf, $car0Base + 7, $nameBytes.Length)

# Car 1: name = "Hamilton"
$car1Base = 30 + 57
$partBuf[$car1Base + 0] = 0
$partBuf[$car1Base + 3] = 2    # teamId = 2 (Ferrari)
$partBuf[$car1Base + 5] = 44   # raceNumber = 44
$nameBytes2 = [System.Text.Encoding]::UTF8.GetBytes("Hamilton")
[Array]::Copy($nameBytes2, 0, $partBuf, $car1Base + 7, $nameBytes2.Length)

$partType = $asm.GetType("Overtake.SimHub.Plugin.Packets.ParticipantsData")
$parts = Invoke-Parse $partType $partBuf

Assert-Equal "NumActiveCars" 2 $parts.NumActiveCars
Assert-Equal "Car0.Name" "Verstappen" $parts.Entries[0].Name
Assert-Equal "Car0.TeamId" 1 $parts.Entries[0].TeamId
Assert-Equal "Car0.RaceNumber" 1 $parts.Entries[0].RaceNumber
Assert-Equal "Car1.Name" "Hamilton" $parts.Entries[1].Name
Assert-Equal "Tag[0]" "Verstappen" $parts.TagsByCarIdx[0]
Assert-Equal "Tag[1]" "Hamilton" $parts.TagsByCarIdx[1]

# ── Test 6: FinalClassification (Packet 8) ──
Write-Host "`n=== Test: FinalClassificationData (Packet 8) ===" -ForegroundColor Cyan
$fcBuf = New-Object byte[] (29 + 1 + 46 * 2)
Make-Header $fcBuf 8 ([uint64]888)
$fcBuf[29] = 2   # numCars

$row0 = 30
$fcBuf[$row0 + 0] = 1   # position
$fcBuf[$row0 + 1] = 58  # numLaps
$fcBuf[$row0 + 2] = 1   # gridPosition
$fcBuf[$row0 + 3] = 25  # points
$fcBuf[$row0 + 4] = 2   # numPitStops
$fcBuf[$row0 + 5] = 3   # resultStatus = Finished
Write-UInt32 $fcBuf ($row0 + 7) ([uint32]91234)  # bestLapTimeMs
Write-Double $fcBuf ($row0 + 11) ([double]5400.123)  # totalRaceTimeSec

$fcType = $asm.GetType("Overtake.SimHub.Plugin.Packets.FinalClassificationData")
$fc = Invoke-Parse $fcType $fcBuf

Assert-Equal "FC.NumCars" 2 $fc.NumCars
Assert-Equal "FC.Car0.Position" 1 $fc.Classification[0].Position
Assert-Equal "FC.Car0.NumLaps" 58 $fc.Classification[0].NumLaps
Assert-Equal "FC.Car0.Points" 25 $fc.Classification[0].Points
Assert-Equal "FC.Car0.BestLapTimeMs" ([uint32]91234) $fc.Classification[0].BestLapTimeMs
$raceTimeDiff = [Math]::Abs($fc.Classification[0].TotalRaceTimeSec - 5400.123)
if ($raceTimeDiff -lt 0.01) {
    Write-Host "  [PASS] FC.Car0.TotalRaceTimeSec ~= 5400.123" -ForegroundColor Green; $pass++
} else {
    Write-Host "  [FAIL] FC.Car0.TotalRaceTimeSec = $($fc.Classification[0].TotalRaceTimeSec)" -ForegroundColor Red; $fail++
}

# ── Test 7: CarDamage (Packet 10) ──
Write-Host "`n=== Test: CarDamageEntry (Packet 10) ===" -ForegroundColor Cyan
$dmgBuf = New-Object byte[] (29 + 46 * 22)
Make-Header $dmgBuf 10 ([uint64]1010)

$d0 = 29
Write-Float $dmgBuf ($d0 + 0) ([float]15.5)   # tyreWear RL
Write-Float $dmgBuf ($d0 + 4) ([float]16.2)   # tyreWear RR
Write-Float $dmgBuf ($d0 + 8) ([float]20.1)   # tyreWear FL
Write-Float $dmgBuf ($d0 + 12) ([float]19.8)  # tyreWear FR
$dmgBuf[$d0 + 28] = 5    # frontLeftWingDamage
$dmgBuf[$d0 + 29] = 10   # frontRightWingDamage
$dmgBuf[$d0 + 30] = 0    # rearWingDamage

$dmgType = $asm.GetType("Overtake.SimHub.Plugin.Packets.CarDamageEntry")
$dmg = Invoke-Parse $dmgType $dmgBuf

$wearDiff = [Math]::Abs($dmg[0].TyreWear.RL - 15.5)
if ($wearDiff -lt 0.1) {
    Write-Host "  [PASS] Car0.TyreWear.RL ~= 15.5" -ForegroundColor Green; $pass++
} else {
    Write-Host "  [FAIL] Car0.TyreWear.RL = $($dmg[0].TyreWear.RL)" -ForegroundColor Red; $fail++
}
Assert-Equal "Car0.Wing.FrontLeft" 5 $dmg[0].Wing.FrontLeft
Assert-Equal "Car0.Wing.FrontRight" 10 $dmg[0].Wing.FrontRight
Assert-Equal "Car0.Wing.Rear" 0 $dmg[0].Wing.Rear

# ── Test 8: SessionHistory (Packet 11) ──
Write-Host "`n=== Test: SessionHistoryData (Packet 11) ===" -ForegroundColor Cyan
$shBuf = New-Object byte[] (29 + 7 + 14 * 100 + 3 * 8)
Make-Header $shBuf 11 ([uint64]1111)

$shp = 29
$shBuf[$shp + 0] = 3    # carIdx
$shBuf[$shp + 1] = 2    # numLaps
$shBuf[$shp + 2] = 1    # numTyreStints
$shBuf[$shp + 3] = 1    # bestLapTimeLapNum

# Lap 1 (offset = 29 + 7 + 0*14 = 36)
$lap1Off = $shp + 7
Write-UInt32 $shBuf $lap1Off ([uint32]91234)        # lapTimeMs
Write-UInt16 $shBuf ($lap1Off + 4) ([uint16]30500)  # s1MsPart
$shBuf[$lap1Off + 6] = 0                             # s1MinPart
Write-UInt16 $shBuf ($lap1Off + 7) ([uint16]1234)   # s2MsPart
$shBuf[$lap1Off + 9] = 1                             # s2MinPart
Write-UInt16 $shBuf ($lap1Off + 10) ([uint16]500)   # s3MsPart
$shBuf[$lap1Off + 12] = 0                            # s3MinPart
$shBuf[$lap1Off + 13] = 15                           # validFlags

# Lap 2
$lap2Off = $lap1Off + 14
Write-UInt32 $shBuf $lap2Off ([uint32]92000)
Write-UInt16 $shBuf ($lap2Off + 4) ([uint16]31000)

# Tyre stint 1 (after 100 laps)
$stintOff = $shp + 7 + 14 * 100
$shBuf[$stintOff + 0] = 10   # endLap
$shBuf[$stintOff + 1] = 16   # tyreActual (soft)
$shBuf[$stintOff + 2] = 16   # tyreVisual

$shType = $asm.GetType("Overtake.SimHub.Plugin.Packets.SessionHistoryData")
$sh = Invoke-Parse $shType $shBuf

Assert-Equal "SH.CarIdx" 3 $sh.CarIdx
Assert-Equal "SH.NumLaps" 2 $sh.NumLaps
Assert-Equal "SH.Laps.Length" 2 $sh.Laps.Length
Assert-Equal "SH.Laps[0].LapTimeMs" ([uint32]91234) $sh.Laps[0].LapTimeMs
Assert-Equal "SH.Laps[0].Sector1Ms" 30500 $sh.Laps[0].Sector1Ms
Assert-Equal "SH.Laps[0].Sector2Ms" 61234 $sh.Laps[0].Sector2Ms
Assert-Equal "SH.Laps[0].Sector3Ms" 500 $sh.Laps[0].Sector3Ms
Assert-Equal "SH.Laps[0].ValidFlags" 15 $sh.Laps[0].ValidFlags
Assert-Equal "SH.TyreStints.Length" 1 $sh.TyreStints.Length
Assert-Equal "SH.TyreStints[0].EndLap" 10 $sh.TyreStints[0].EndLap
Assert-Equal "SH.TyreStints[0].TyreActual" 16 $sh.TyreStints[0].TyreActual
Assert-Equal "SH.Best.BestLapTimeLapNum" 1 $sh.Best.BestLapTimeLapNum
Assert-Equal "SH.Best.BestLapTimeMs" ([uint32]91234) $sh.Best.BestLapTimeMs

# ── Test 9: PacketParser.Dispatch ──
Write-Host "`n=== Test: PacketParser.Dispatch ===" -ForegroundColor Cyan
$dispatchType = $asm.GetType("Overtake.SimHub.Plugin.Parsers.PacketParser")

# Dispatch a session packet
$result = Invoke-Parse $dispatchType $sessionBuf "Dispatch"
Assert-Equal "Dispatch.Header.PacketId" 1 $result.Header.PacketId
if ($null -ne $result.Session) {
    Write-Host "  [PASS] Dispatch.Session is not null" -ForegroundColor Green; $pass++
} else {
    Write-Host "  [FAIL] Dispatch.Session is null" -ForegroundColor Red; $fail++
}

# Dispatch unknown packet type (ID 5 = CarSetups, not parsed)
$unknownBuf = New-Object byte[] 100
Make-Header $unknownBuf 5 ([uint64]555)
$unkResult = Invoke-Parse $dispatchType $unknownBuf "Dispatch"
if ($null -ne $unkResult -and $null -ne $unkResult.Header) {
    Write-Host "  [PASS] Unknown packet returns header-only result" -ForegroundColor Green; $pass++
} else {
    Write-Host "  [FAIL] Unknown packet handling" -ForegroundColor Red; $fail++
}

# ── Summary ──
Write-Host "`n========================================" -ForegroundColor White
Write-Host "  PASSED: $pass   FAILED: $fail" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
Write-Host "========================================" -ForegroundColor White

if ($fail -gt 0) { exit 1 }
