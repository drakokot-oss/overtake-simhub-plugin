param([string]$Path)
$ErrorActionPreference = "Stop"
$json = Get-Content $Path -Raw | ConvertFrom-Json
$filename = Split-Path $Path -Leaf

Write-Host ""
Write-Host ("=" * 60) -ForegroundColor Cyan
Write-Host "  $filename" -ForegroundColor Cyan
Write-Host ("=" * 60) -ForegroundColor Cyan

Write-Host "Schema: $($json.schemaVersion) | Game: $($json.game)"
Write-Host "Capture UID: $($json.capture.sessionUID)"
Write-Host "Session types: $($json.capture.sessionTypesInCapture -join ', ')"
Write-Host "Participants ($($json.participants.Count)): $($json.participants -join ', ')"

foreach ($sess in $json.sessions) {
    $stName = $sess.sessionType.name
    Write-Host ""
    Write-Host ("--- {0} (UID={1}) ---" -f $stName, $sess.sessionUID) -ForegroundColor Yellow
    Write-Host ("  Track: {0} (id={1}) | Network: {2}" -f $sess.track.name, $sess.track.id, $sess.networkGame)
    Write-Host ("  Weather: {0} | Track: {1}C | Air: {2}C" -f $sess.weather.name, $sess.trackTempC, $sess.airTempC)
    
    $sc = $sess.safetyCar
    Write-Host ("  SC: full={0} vsc={1} redFlag={2} | lapsUnderSC=[{3}] lapsUnderVSC=[{4}]" -f $sc.fullDeploys, $sc.vscDeploys, $sc.redFlagPeriods, ($sc.lapsUnderSC -join ','), ($sc.lapsUnderVSC -join ','))

    # Results
    $results = $sess.results
    Write-Host ("  Results: {0} drivers" -f $results.Count) -ForegroundColor Green
    $nullGridCount = 0
    foreach ($r in $results) {
        $gridStr = if ($r.grid -ne $null) { "G$($r.grid)" } else { "G=null"; $nullGridCount++ }
        $teamStr = if ($r.teamName) { $r.teamName } else { "NO_TEAM" }
        $statusTag = if ($r.status -ne "Finished") { " [$($r.status)]" } else { "" }
        Write-Host ("    P{0,-2} {1,-22} {2,-30} {3,-7} Laps:{4,-3} Best:{5}{6}" -f $r.position, $r.tag, $teamStr, $gridStr, $r.numLaps, $r.bestLapTime, $statusTag)
    }
    if ($nullGridCount -gt 0) {
        Write-Host ("  WARNING: {0} drivers with null grid!" -f $nullGridCount) -ForegroundColor Red
    }

    # Drivers
    $dps = $sess.drivers | Get-Member -MemberType NoteProperty
    Write-Host ("  Drivers detailed: {0}" -f $dps.Count) -ForegroundColor Green
    $noTeamCount = 0
    foreach ($dp in $dps) {
        $d = $sess.drivers.($dp.Name)
        $teamStr = if ($d.teamName) { $d.teamName } else { "NO_TEAM"; $noTeamCount++ }
        $teamIdStr = if ($d.teamId -ne $null) { $d.teamId } else { "null" }
        $lapCount = if ($d.laps) { $d.laps.Count } else { 0 }
        $isPlayer = if ($d.isPlayer) { " [PLAYER]" } else { "" }
        $ai = if ($d.aiControlled) { " [AI]" } else { "" }
        Write-Host ("    {0,-22} teamId={1,-4} team={2,-30} laps={3,-3}{4}{5}" -f $dp.Name, $teamIdStr, $teamStr, $lapCount, $isPlayer, $ai)
    }
    if ($noTeamCount -gt 0) {
        Write-Host ("  WARNING: {0} drivers with NO team!" -f $noTeamCount) -ForegroundColor Red
    }

    # Awards
    $aw = $sess.awards
    if ($aw) {
        $fl = if ($aw.fastestLap) { "$($aw.fastestLap.tag) $($aw.fastestLap.time)" } else { "null" }
        $mc = if ($aw.mostConsistent) { "$($aw.mostConsistent.tag) stdDev=$($aw.mostConsistent.stdDev)" } else { "null" }
        $mp = if ($aw.mostPositionsGained) { "$($aw.mostPositionsGained.tag) +$($aw.mostPositionsGained.gained)" } else { "null" }
        Write-Host ("  Awards: FL={0} | MC={1} | MP={2}" -f $fl, $mc, $mp)
    }

    # Event summary
    $events = $sess.events
    if ($events.Count -gt 0) {
        $codes = @{}
        foreach ($ev in $events) { $c = $ev.code; if (-not $codes[$c]) { $codes[$c] = 0 }; $codes[$c]++ }
        $codeStr = ($codes.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name):$($_.Value)" }) -join " "
        Write-Host ("  Events ({0}): {1}" -f $events.Count, $codeStr)
    }
}

# Debug
if ($json._debug) {
    Write-Host ""
    Write-Host "--- DEBUG ---" -ForegroundColor DarkGray
    if ($json._debug.diagnostics -and $json._debug.diagnostics.participants) {
        $p = $json._debug.diagnostics.participants
        Write-Host ("  Participants: received={0} numActive={1} playerCarIdx={2} recovered={3}" -f $p.received, $p.numActive, $p.playerCarIdx, $p.playerRecoveredFromOverflow) -ForegroundColor DarkGray
    }
}
Write-Host ""
