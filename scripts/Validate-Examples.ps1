$ErrorActionPreference = "Stop"
$dir = (Resolve-Path "$PSScriptRoot\..\examples").Path
$files = Get-ChildItem $dir -Filter "*.json"

foreach ($f in $files) {
    $json = Get-Content $f.FullName -Raw | ConvertFrom-Json
    $sessCount = $json.sessions.Count
    $rSess = $json.sessions | Where-Object { $_.sessionType.name -eq "Race" }
    $resCount = $rSess.results.Count
    $drvCount = ($rSess.drivers | Get-Member -MemberType NoteProperty).Count
    $evtCount = $rSess.events.Count
    $scLaps = if ($rSess.safetyCar.lapsUnderSC.Count -gt 0) { $rSess.safetyCar.lapsUnderSC -join "," } else { "none" }
    $vscLaps = if ($rSess.safetyCar.lapsUnderVSC.Count -gt 0) { $rSess.safetyCar.lapsUnderVSC -join "," } else { "none" }
    $player = $rSess.drivers.PSObject.Properties | Where-Object { $_.Value.isPlayer -eq $true } | Select-Object -First 1
    $playerTag = if ($player) { $player.Name } else { "none" }
    $awards = @()
    if ($rSess.awards.fastestLap) { $awards += "FL:" + $rSess.awards.fastestLap.tag }
    if ($rSess.awards.mostConsistent) { $awards += "MC:" + $rSess.awards.mostConsistent.tag }
    if ($rSess.awards.mostPositionsGained) { $awards += "PG:" + $rSess.awards.mostPositionsGained.tag }
    $dnf = ($rSess.results | Where-Object { $_.status -ne "Finished" }).Count
    $pens = ($rSess.events | Where-Object { $_.code -eq "PENA" }).Count
    $colls = ($rSess.events | Where-Object { $_.code -eq "COLL" }).Count
    $winner = $rSess.results[0].tag

    Write-Host ""
    Write-Host $f.Name -ForegroundColor Cyan
    Write-Host ("  Sessions: {0} | Results: {1} | Drivers: {2} | Events: {3}" -f $sessCount, $resCount, $drvCount, $evtCount)
    Write-Host ("  SC laps: [{0}] | VSC laps: [{1}]" -f $scLaps, $vscLaps)
    Write-Host ("  Player: {0} | DNFs: {1} | Penalties: {2} | Collisions: {3}" -f $playerTag, $dnf, $pens, $colls)
    Write-Host ("  Awards: {0}" -f ($awards -join " | "))
    Write-Host ("  Winner: {0}" -f $winner)

    # Validate key fields
    $errors = @()
    if ($json.schemaVersion -ne "league-1.0") { $errors += "Wrong schemaVersion" }
    if ($json.game -ne "F1_25") { $errors += "Wrong game" }
    if ($sessCount -lt 2) { $errors += "Expected 2 sessions (Quali+Race)" }
    if ($resCount -lt 15) { $errors += "Too few results" }
    if ($drvCount -lt 15) { $errors += "Too few drivers" }
    if (-not $rSess.awards.fastestLap) { $errors += "Missing fastestLap award" }
    $firstDriver = ($rSess.drivers.PSObject.Properties | Select-Object -First 1).Value
    if (-not $firstDriver.laps -or $firstDriver.laps.Count -lt 5) { $errors += "Too few laps for first driver" }
    if (-not $firstDriver.tyreStints -or $firstDriver.tyreStints.Count -lt 1) { $errors += "Missing tyreStints" }
    if (-not $firstDriver.tyreWearPerLap -or $firstDriver.tyreWearPerLap.Count -lt 1) { $errors += "Missing tyreWearPerLap" }
    if (-not $firstDriver.damagePerLap -or $firstDriver.damagePerLap.Count -lt 1) { $errors += "Missing damagePerLap" }
    if ($null -eq $firstDriver.isPlayer) { $errors += "Missing isPlayer field" }

    if ($errors.Count -gt 0) {
        Write-Host "  ERRORS:" -ForegroundColor Red
        foreach ($e in $errors) { Write-Host "    - $e" -ForegroundColor Red }
    } else {
        Write-Host "  VALID" -ForegroundColor Green
    }
}
Write-Host ""
