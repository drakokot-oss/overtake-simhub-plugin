$ErrorActionPreference = "Stop"
$json = Get-Content "d:\Drako\Overtake\Overtake Telemetry\output\Spa_20260218_152737_BB20D0.json" -Raw | ConvertFrom-Json
$race = $json.sessions | Where-Object { $_.sessionType.name -eq "Race" }

Write-Host "=== Tyre Wear (Player_0 all laps) ===" -ForegroundColor Cyan
$p0 = $race.drivers.Player_0
if ($p0 -and $p0.tyreWearPerLap) {
    foreach ($w in $p0.tyreWearPerLap) {
        Write-Host ("  lap={0} rl={1:N1} rr={2:N1} fl={3:N1} fr={4:N1} avg={5:N1}" -f $w.lapNumber, $w.rl, $w.rr, $w.fl, $w.fr, $w.avg)
    }
} else { Write-Host "  No tyre wear data" -ForegroundColor Red }

Write-Host ""
Write-Host "=== Tyre Stints (Player_0) ===" -ForegroundColor Cyan
if ($p0 -and $p0.tyreStints) {
    foreach ($ts in $p0.tyreStints) {
        Write-Host ("  stint={0} compound={1} startLap={2} endLap={3} age={4}" -f $ts.stintIndex, $ts.compound, $ts.startLap, $ts.endLap, $ts.age)
    }
}

Write-Host ""
Write-Host "=== Penalty data (Player_3 - most pens) ===" -ForegroundColor Cyan
$p3 = $race.drivers.Player_3
if ($p3 -and $p3.penaltiesTimeline -and $p3.penaltiesTimeline.Count -gt 0) {
    foreach ($pen in $p3.penaltiesTimeline) {
        Write-Host ("  ts={0} type={1} offense={2} lapNum={3}" -f $pen.tsMs, $pen.penaltyType, $pen.infringementType, $pen.lapNum)
    }
} else { Write-Host "  No penalties timeline" -ForegroundColor DarkYellow }

Write-Host ""
Write-Host "=== Collision data (Player_8) ===" -ForegroundColor Cyan
$p8 = $race.drivers.Player_8
if ($p8 -and $p8.collisionsTimeline -and $p8.collisionsTimeline.Count -gt 0) {
    foreach ($c in $p8.collisionsTimeline) {
        Write-Host ("  ts={0} otherVehicle={1}" -f $c.tsMs, $c.otherVehicleTag)
    }
} else { Write-Host "  No collision data" -ForegroundColor DarkYellow }

Write-Host ""
Write-Host "=== Collision data (Player_2) ===" -ForegroundColor Cyan
$p2c = $race.drivers.Player_2
if ($p2c -and $p2c.collisionsTimeline -and $p2c.collisionsTimeline.Count -gt 0) {
    foreach ($c in $p2c.collisionsTimeline) {
        Write-Host ("  ts={0} otherVehicle={1}" -f $c.tsMs, $c.otherVehicleTag)
    }
} else { Write-Host "  No collision data" -ForegroundColor DarkYellow }

Write-Host ""
Write-Host "=== PitStops Timeline (frostaczek - 3 stops) ===" -ForegroundColor Cyan
$fro = $race.drivers.frostaczek
if ($fro -and $fro.pitStopsTimeline -and $fro.pitStopsTimeline.Count -gt 0) {
    foreach ($pit in $fro.pitStopsTimeline) {
        Write-Host ("  lap={0} ts={1} pitDuration={2}" -f $pit.lapNumber, $pit.tsMs, $pit.pitLaneTimeSec)
    }
}

Write-Host ""
Write-Host "=== Lap times (Player_0 first 5 laps) ===" -ForegroundColor Cyan
if ($p0 -and $p0.laps) {
    $p0.laps | Select-Object -First 5 | ForEach-Object {
        Write-Host ("  lap={0} time={1} sector1={2} sector2={3} sector3={4} pos={5}" -f $_.lapNumber, $_.lapTime, $_.sector1Time, $_.sector2Time, $_.sector3Time, $_.carPosition)
    }
}

Write-Host ""
Write-Host "=== Players who joined during Race but not in Quali ===" -ForegroundColor Cyan
$quali = $json.sessions | Where-Object { $_.sessionType.name -eq "OneShotQualifying" }
$qualiDrivers = ($quali.drivers | Get-Member -MemberType NoteProperty).Name
$raceDrivers = ($race.drivers | Get-Member -MemberType NoteProperty).Name
$onlyRace = $raceDrivers | Where-Object { $_ -notin $qualiDrivers }
Write-Host "  Only in Race: $($onlyRace -join ', ')"

Write-Host ""
Write-Host "=== Weather Forecast ===" -ForegroundColor Cyan
if ($race.weatherForecast -and $race.weatherForecast.Count -gt 0) {
    foreach ($wf in $race.weatherForecast) {
        Write-Host ("  offset={0}min weather={1} temp={2}C rain={3}" -f $wf.timeOffsetMinutes, $wf.weather.name, $wf.airTempC, $wf.rainPercentage)
    }
} else { Write-Host "  No forecast data" -ForegroundColor DarkYellow }

Write-Host ""
Write-Host "=== RTMT (Retirement) events ===" -ForegroundColor Cyan
$rtmts = $race.events | Where-Object { $_.code -eq "RTMT" }
foreach ($r in $rtmts) {
    Write-Host ("  ts={0} vehicleTag={1}" -f $r.tsMs, $r.data.vehicleTag)
}

Write-Host ""
Write-Host "=== Full events list (Race, first 30) ===" -ForegroundColor Cyan
$race.events | Select-Object -First 30 | ForEach-Object {
    $dataStr = ""
    if ($_.data) {
        if ($_.data.vehicleTag) { $dataStr += " veh=$($_.data.vehicleTag)" }
        if ($_.data.otherVehicleTag) { $dataStr += " other=$($_.data.otherVehicleTag)" }
        if ($_.data.safetyCarType -ne $null) { $dataStr += " scType=$($_.data.safetyCarType)" }
        if ($_.data.eventType -ne $null) { $dataStr += " evType=$($_.data.eventType)" }
    }
    Write-Host ("  ts={0} code={1}{2}" -f $_.tsMs, $_.code, $dataStr)
}

Write-Host ""
Write-Host "=== _debug section ===" -ForegroundColor Cyan
if ($json._debug) {
    $json._debug | ConvertTo-Json -Depth 5 | Write-Host
}
