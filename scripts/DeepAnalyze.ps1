$ErrorActionPreference = "Stop"
$json = Get-Content "d:\Drako\Overtake\Overtake Telemetry\output\Spa_20260218_152737_BB20D0.json" -Raw | ConvertFrom-Json
$race = $json.sessions | Where-Object { $_.sessionType.name -eq "Race" }

Write-Host "=== SCAR EVENTS ===" -ForegroundColor Cyan
$scars = $race.events | Where-Object { $_.code -eq "SCAR" }
foreach ($s in $scars) {
    Write-Host ("  ts={0} safetyCarType={1} eventType={2}" -f $s.tsMs, $s.data.safetyCarType, $s.data.eventType)
}

Write-Host ""
Write-Host "=== LGOT / CHQF ===" -ForegroundColor Cyan
$lgot = $race.events | Where-Object { $_.code -eq "LGOT" }
$chqf = $race.events | Where-Object { $_.code -eq "CHQF" }
if ($lgot) { Write-Host ("  LGOT: {0}" -f $lgot.tsMs) -ForegroundColor Green }
else { Write-Host "  LGOT: NOT FOUND" -ForegroundColor Red }
if ($chqf) { Write-Host ("  CHQF: {0}" -f $chqf.tsMs) -ForegroundColor Green }
else { Write-Host "  CHQF: NOT FOUND" -ForegroundColor Red }

Write-Host ""
Write-Host "=== lapsUnderSC / lapsUnderVSC ===" -ForegroundColor Cyan
Write-Host ("  SC laps: [{0}]" -f ($race.safetyCar.lapsUnderSC -join ","))
Write-Host ("  VSC laps: [{0}]" -f ($race.safetyCar.lapsUnderVSC -join ","))
Write-Host ("  fullDeploys={0} vscDeploys={1}" -f $race.safetyCar.fullDeploys, $race.safetyCar.vscDeploys)

Write-Host ""
Write-Host "=== isPlayer ===" -ForegroundColor Cyan
$found = $false
$dps = $race.drivers | Get-Member -MemberType NoteProperty
foreach ($dp in $dps) {
    $d = $race.drivers.($dp.Name)
    if ($d.isPlayer -eq $true) { Write-Host ("  {0} isPlayer=TRUE" -f $dp.Name) -ForegroundColor Green; $found = $true }
}
if (-not $found) { Write-Host "  NO PLAYER FOUND" -ForegroundColor Red }

Write-Host ""
Write-Host "=== aiControlled flags ===" -ForegroundColor Cyan
foreach ($dp in $dps) {
    $d = $race.drivers.($dp.Name)
    $tag = if ($d.aiControlled) { "[AI]" } else { "[HUMAN]" }
    Write-Host ("  {0,-20} {1,-8} showNames={2} platform={3}" -f $dp.Name, $tag, $d.showOnlineNames, $d.platform)
}

Write-Host ""
Write-Host "=== Grid positions (Race results) ===" -ForegroundColor Cyan
foreach ($r in $race.results) {
    $g = if ($r.grid) { $r.grid } else { "null" }
    Write-Host ("  P{0,-2} {1,-20} grid={2}" -f $r.position, $r.tag, $g)
}

Write-Host ""
Write-Host "=== Phantom session check ===" -ForegroundColor Cyan
foreach ($sess in $json.sessions) {
    $evCount = $sess.events.Count
    $drCount = ($sess.drivers | Get-Member -MemberType NoteProperty -ErrorAction SilentlyContinue).Count
    $resCount = $sess.results.Count
    Write-Host ("  UID={0} type={1} events={2} drivers={3} results={4}" -f $sess.sessionUID, $sess.sessionType.name, $evCount, $drCount, $resCount)
}

Write-Host ""
Write-Host "=== Wear data sample (Player_0 first 3 laps) ===" -ForegroundColor Cyan
$p0 = $race.drivers.Player_0
if ($p0 -and $p0.tyreWearPerLap) {
    $p0.tyreWearPerLap | Select-Object -First 3 | ForEach-Object {
        Write-Host ("  lap={0} rl={1} rr={2} fl={3} fr={4} avg={5}" -f $_.lapNumber, $_.rl, $_.rr, $_.fl, $_.fr, $_.avg)
    }
}

Write-Host ""
Write-Host "=== Damage data sample (Player_8 - most collisions) ===" -ForegroundColor Cyan
$p8 = $race.drivers.Player_8
if ($p8 -and $p8.damagePerLap) {
    $nonZero = $p8.damagePerLap | Where-Object { $_.wingFL -gt 0 -or $_.wingFR -gt 0 -or $_.wingRear -gt 0 }
    if ($nonZero) {
        foreach ($d in $nonZero) {
            Write-Host ("  lap={0} FL={1} FR={2} Rear={3}" -f $d.lapNumber, $d.wingFL, $d.wingFR, $d.wingRear)
        }
    } else { Write-Host "  No wing damage recorded" -ForegroundColor DarkYellow }
}

Write-Host ""
Write-Host "=== Qualifying session details ===" -ForegroundColor Cyan
$quali = $json.sessions | Where-Object { $_.sessionType.name -eq "OneShotQualifying" }
if ($quali) {
    Write-Host ("  Results: {0} | Drivers: {1} | Events: {2}" -f $quali.results.Count, ($quali.drivers | Get-Member -MemberType NoteProperty).Count, $quali.events.Count)
    Write-Host "  Qualifying results:" -ForegroundColor White
    foreach ($r in $quali.results) {
        Write-Host ("    P{0} {1} - {2}" -f $r.position, $r.tag, $r.bestLapTime)
    }
}
