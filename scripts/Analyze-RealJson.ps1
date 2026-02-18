param([string]$Path)
$ErrorActionPreference = "Stop"
$json = Get-Content $Path -Raw | ConvertFrom-Json

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  JSON Analysis: $(Split-Path $Path -Leaf)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

Write-Host ""
Write-Host "Schema: $($json.schemaVersion) | Game: $($json.game)" -ForegroundColor White
Write-Host "Capture UID: $($json.capture.sessionUID)" -ForegroundColor Gray
Write-Host "Session types: $($json.capture.sessionTypesInCapture -join ', ')" -ForegroundColor Gray
Write-Host "Participants: $($json.participants.Count) -> $($json.participants -join ', ')" -ForegroundColor Gray

Write-Host ""
Write-Host "--- SESSIONS ($($json.sessions.Count)) ---" -ForegroundColor Yellow

for ($i = 0; $i -lt $json.sessions.Count; $i++) {
    $s = $json.sessions[$i]
    Write-Host ""
    Write-Host "  Session $($i+1): $($s.sessionType.name)" -ForegroundColor Cyan
    Write-Host "    UID: $($s.sessionUID)" -ForegroundColor Gray
    Write-Host "    Track: $($s.track.name) (id=$($s.track.id))" -ForegroundColor White
    Write-Host "    Weather: $($s.weather.name) | Track: $($s.trackTempC)C | Air: $($s.airTempC)C" -ForegroundColor White
    Write-Host "    Network: $($s.networkGame)" -ForegroundColor White

    # Safety Car
    $sc = $s.safetyCar
    Write-Host "    SafetyCar: status=$($sc.status.name) | SC=$($sc.fullDeploys) | VSC=$($sc.vscDeploys) | RedFlag=$($sc.redFlagPeriods)" -ForegroundColor White
    if ($sc.lapsUnderSC.Count -gt 0) { Write-Host "    SC Laps: [$($sc.lapsUnderSC -join ',')]" -ForegroundColor Yellow }
    if ($sc.lapsUnderVSC.Count -gt 0) { Write-Host "    VSC Laps: [$($sc.lapsUnderVSC -join ',')]" -ForegroundColor Yellow }

    # Weather timeline
    if ($s.weatherTimeline.Count -gt 0) {
        Write-Host "    Weather Timeline: $($s.weatherTimeline.Count) entries" -ForegroundColor Gray
        foreach ($wt in $s.weatherTimeline) {
            Write-Host "      ts=$($wt.tsMs) -> $($wt.weather.name) track=$($wt.trackTempC)C" -ForegroundColor DarkGray
        }
    }

    # Results
    $results = $s.results
    if ($results.Count -gt 0) {
        Write-Host "    Results: $($results.Count) drivers" -ForegroundColor Green
        foreach ($r in $results) {
            $statusTag = ""
            if ($r.status -ne "Finished") { $statusTag = " [$($r.status)]" }
            $penTag = ""
            if ($r.penaltiesTimeSec -gt 0) { $penTag = " +$($r.penaltiesTimeSec)s pen" }
            Write-Host ("      P{0,-2} {1,-20} {2,-25} Grid:{3,-3} Laps:{4,-3} Best:{5} Pits:{6}{7}{8}" -f $r.position, $r.tag, $r.teamName, $r.grid, $r.numLaps, $r.bestLapTime, $r.pitStops, $penTag, $statusTag) -ForegroundColor White
        }
    } else {
        Write-Host "    Results: EMPTY" -ForegroundColor DarkYellow
    }

    # Drivers detail
    $driverProps = $s.drivers | Get-Member -MemberType NoteProperty
    Write-Host "    Drivers detailed: $($driverProps.Count)" -ForegroundColor Green
    foreach ($dp in $driverProps) {
        $d = $s.drivers.($dp.Name)
        $lapCount = if ($d.laps) { $d.laps.Count } else { 0 }
        $stintCount = if ($d.tyreStints) { $d.tyreStints.Count } else { 0 }
        $wearCount = if ($d.tyreWearPerLap) { $d.tyreWearPerLap.Count } else { 0 }
        $dmgCount = if ($d.damagePerLap) { $d.damagePerLap.Count } else { 0 }
        $repairCount = if ($d.wingRepairs) { $d.wingRepairs.Count } else { 0 }
        $penCount = if ($d.penaltiesTimeline) { $d.penaltiesTimeline.Count } else { 0 }
        $collCount = if ($d.collisionsTimeline) { $d.collisionsTimeline.Count } else { 0 }
        $pitCount = if ($d.pitStopsTimeline) { $d.pitStopsTimeline.Count } else { 0 }
        $isPlayer = if ($d.isPlayer) { " [PLAYER]" } else { "" }
        $ai = if ($d.aiControlled) { " [AI]" } else { "" }
        Write-Host ("      {0,-20} team={1,-3} laps={2,-3} stints={3} wear={4} dmg={5} repairs={6} pens={7} colls={8} pits={9}{10}{11}" -f $dp.Name, $d.teamId, $lapCount, $stintCount, $wearCount, $dmgCount, $repairCount, $penCount, $collCount, $pitCount, $isPlayer, $ai) -ForegroundColor Gray
    }

    # Awards
    $aw = $s.awards
    if ($aw) {
        Write-Host "    Awards:" -ForegroundColor Green
        if ($aw.fastestLap) { Write-Host "      Fastest Lap: $($aw.fastestLap.tag) - $($aw.fastestLap.time)" -ForegroundColor White }
        else { Write-Host "      Fastest Lap: null" -ForegroundColor DarkYellow }
        if ($aw.mostConsistent) { Write-Host "      Most Consistent: $($aw.mostConsistent.tag) - stdDev=$($aw.mostConsistent.stdDev)" -ForegroundColor White }
        else { Write-Host "      Most Consistent: null" -ForegroundColor DarkYellow }
        if ($aw.mostPositionsGained) { Write-Host "      Most Positions: $($aw.mostPositionsGained.tag) - gained=$($aw.mostPositionsGained.gained) (P$($aw.mostPositionsGained.grid)->P$($aw.mostPositionsGained.finish))" -ForegroundColor White }
        else { Write-Host "      Most Positions: null" -ForegroundColor DarkYellow }
    }

    # Events
    $events = $s.events
    if ($events.Count -gt 0) {
        $codes = @{}
        foreach ($ev in $events) { $c = $ev.code; if (-not $codes[$c]) { $codes[$c] = 0 }; $codes[$c]++ }
        $codeStr = ($codes.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name):$($_.Value)" }) -join " "
        Write-Host "    Events: $($events.Count) total -> $codeStr" -ForegroundColor Gray
    }
}

# Debug section
if ($json._debug) {
    Write-Host ""
    Write-Host "--- DEBUG ---" -ForegroundColor DarkGray
    Write-Host "  Packets: $($json._debug.packets)" -ForegroundColor DarkGray
    if ($json._debug.participants) {
        $p = $json._debug.participants
        Write-Host "  Participants: received=$($p.received) numActive=$($p.numActive) playerCarIdx=$($p.playerCarIdx) recovered=$($p.playerRecoveredFromOverflow)" -ForegroundColor DarkGray
    }
}

Write-Host ""
