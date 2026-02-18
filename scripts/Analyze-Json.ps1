[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue

$jsonPath = $args[0]
if (-not $jsonPath) { $jsonPath = "d:\Drako\Overtake\Overtake Telemetry\output\league_8526981694939562817_1771393856829.json" }

$json = Get-Content $jsonPath -Raw
$d = $ser.DeserializeObject($json)

Write-Host "=== TOP LEVEL ===" -ForegroundColor Cyan
Write-Host "schemaVersion: $($d['schemaVersion'])"
Write-Host "game: $($d['game'])"
Write-Host "participants: $($d['participants'].Count)"
Write-Host "sessions: $($d['sessions'].Count)"

$cap = $d['capture']
Write-Host "capture.sessionUID: $($cap['sessionUID'])"
Write-Host "capture.source: $(if($cap['source'] -ne $null){'present'}else{'MISSING'})"
Write-Host "capture.sessionTypes: $($cap['sessionTypesInCapture'] -join ', ')"
Write-Host ""

foreach ($s in $d['sessions']) {
    $st = $s['sessionType']
    $tr = $s['track']
    $we = $s['weather']
    Write-Host "=== SESSION: $($st['name']) ===" -ForegroundColor Yellow
    Write-Host "  UID: $($s['sessionUID'])"
    Write-Host "  Track: $($tr['name']) (id=$($tr['id']))"
    Write-Host "  Weather: $($we['name'])"
    Write-Host "  TrackTemp: $($s['trackTempC'])C  AirTemp: $($s['airTempC'])C"
    Write-Host "  NetworkGame: $($s['networkGame'])"
    Write-Host "  Drivers: $($s['drivers'].Count)"
    Write-Host "  Results: $($s['results'].Count)"
    Write-Host "  Events: $($s['events'].Count)"
    Write-Host "  WeatherTimeline: $($s['weatherTimeline'].Count)"
    Write-Host "  WeatherForecast: $($s['weatherForecast'].Count)"
    Write-Host "  SessionEndedAtMs: $($s['sessionEndedAtMs'])"

    # Safety car
    $sc = $s['safetyCar']
    Write-Host "  SafetyCar: fullDeploys=$($sc['fullDeploys']) vsc=$($sc['vscDeploys']) redFlags=$($sc['redFlagPeriods'])"

    # Awards
    $aw = $s['awards']
    if ($aw['fastestLap'] -ne $null) {
        $fl = $aw['fastestLap']
        Write-Host "  Award fastestLap: $($fl['tag']) $($fl['time'])" -ForegroundColor Green
    } else { Write-Host "  Award fastestLap: null" }
    if ($aw['mostPositionsGained'] -ne $null) {
        $mp = $aw['mostPositionsGained']
        Write-Host "  Award mostPosGained: $($mp['tag']) +$($mp['gained']) (P$($mp['grid'])->P$($mp['finish']))" -ForegroundColor Green
    } else { Write-Host "  Award mostPosGained: null" }
    if ($aw['mostConsistent'] -ne $null) {
        $mc = $aw['mostConsistent']
        Write-Host "  Award mostConsistent: $($mc['tag']) stdDev=$($mc['stdDev']) cleanLaps=$($mc['cleanLaps'])" -ForegroundColor Green
    } else { Write-Host "  Award mostConsistent: null" }

    # Results
    if ($s['results'].Count -gt 0) {
        Write-Host "  --- Results ---" -ForegroundColor Cyan
        foreach ($r in $s['results']) {
            $status = $r['status']
            $bl = if ($r['bestLapTime']) { $r['bestLapTime'] } else { "-" }
            Write-Host "    P$($r['position']) $($r['tag']) | Team: $($r['teamName']) | Grid: $($r['grid']) | BestLap: $bl | Status: $status"
        }
    }

    # Sample driver
    if ($s['drivers'].Count -gt 0) {
        $firstTag = ($s['drivers'].Keys | Select-Object -First 1)
        $drv = $s['drivers'][$firstTag]
        Write-Host "  --- Sample Driver: $firstTag ---" -ForegroundColor Cyan
        Write-Host "    position: $($drv['position'])"
        Write-Host "    teamName: $($drv['teamName'])"
        Write-Host "    showOnlineNames: $($drv['showOnlineNames'])"
        Write-Host "    yourTelemetry: $($drv['yourTelemetry'])"
        Write-Host "    laps: $($drv['laps'].Count)"
        Write-Host "    tyreStints: $($drv['tyreStints'].Count)"
        Write-Host "    tyreWearPerLap: $($drv['tyreWearPerLap'].Count)"
        Write-Host "    damagePerLap: $($drv['damagePerLap'].Count)"
        Write-Host "    pitStopsTimeline: $($drv['pitStopsTimeline'].Count)"
        Write-Host "    penaltiesTimeline: $($drv['penaltiesTimeline'].Count)"
        Write-Host "    collisionsTimeline: $($drv['collisionsTimeline'].Count)"
        if ($drv['laps'].Count -gt 0) {
            $lap = $drv['laps'][0]
            Write-Host "    Lap 1: $($lap['lapTime']) s1=$($lap['sector1Ms']) s2=$($lap['sector2Ms']) s3=$($lap['sector3Ms']) valid=$($lap['valid']) flags=$($lap['flags'] -join ',')"
        }
        if ($drv['tyreStints'].Count -gt 0) {
            $stint = $drv['tyreStints'][0]
            Write-Host "    Stint 1: tyreActual=$($stint['tyreActual']) tyreVisual=$($stint['tyreVisual'])"
        }
    }

    # Sample events with data
    $eventsWithData = $s['events'] | Where-Object { $_['data'] -ne $null } | Select-Object -First 3
    if ($eventsWithData.Count -gt 0) {
        Write-Host "  --- Sample Events (with data) ---" -ForegroundColor Cyan
        foreach ($ev in $eventsWithData) {
            $dataKeys = $ev['data'].Keys -join ","
            Write-Host "    $($ev['code']) ($($ev['name'])): keys=[$dataKeys]"
        }
    }

    Write-Host ""
}

# Debug section
if ($d['_debug'] -ne $null) {
    $dbg = $d['_debug']
    Write-Host "=== DEBUG ===" -ForegroundColor Cyan
    Write-Host "  packetIdCounts keys: $($dbg['packetIdCounts'].Count)"
    Write-Host "  notes: $(if($dbg['notes']){'present'}else{'empty'})"
}
