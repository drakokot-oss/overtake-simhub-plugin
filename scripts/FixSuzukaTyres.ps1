param(
    [string]$InputPath = "d:\Drako\Overtake\Overtake Telemetry\output\Suzuka_20260218_220813_484091.json",
    [string]$OutputPath = "d:\Drako\Overtake\Overtake Telemetry\output\Suzuka_20260218_220813_484091_FIXED.json"
)

[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 50MB
$raw = [System.IO.File]::ReadAllText($InputPath)
$j = $ser.DeserializeObject($raw)

$MEDIUM_ACTUAL = 19; $MEDIUM_VISUAL = 17
$HARD_ACTUAL   = 20; $HARD_VISUAL   = 18
$SOFT_ACTUAL   = 18; $SOFT_VISUAL   = 16

$tyreActualNames = @{16="C5"; 17="C4"; 18="C3"; 19="C2"; 20="C1"; 21="C0"; 22="C6"; 7="Intermediate"; 8="Wet"}
$tyreVisualNames = @{16="Soft"; 17="Medium"; 18="Hard"; 7="Intermediate"; 8="Wet"}

$qualiSessionNames = @("ShortQualifying","OneShotQualifying","Qualifying1","Qualifying2","Qualifying3","SprintShootout")

function Filter-AIDriversFromQuali($j) {
    $removed = 0
    foreach ($s in $j["sessions"]) {
        $stName = $s["sessionType"]["name"]
        if ($qualiSessionNames -notcontains $stName) { continue }
        $results = $s["results"]
        $drivers = $s["drivers"]
        if (-not $results -or -not $drivers) { continue }
        $toRemove = [System.Collections.ArrayList]::new()
        foreach ($r in $results) {
            $nl = $r["numLaps"]; if ($null -eq $nl) { $nl = 0 }
            $tag = $r["tag"]
            $lapCount = 0
            if ($drivers[$tag] -and $drivers[$tag]["laps"]) { $lapCount = $drivers[$tag]["laps"].Count }
            $ai = $drivers[$tag]["aiControlled"]
            if ($lapCount -eq 0 -and $nl -eq 0 -and $ai -eq $true) { [void]$toRemove.Add($tag) }
        }
        $newResults = [System.Collections.ArrayList]::new()
        foreach ($r in $results) {
            if ($toRemove -notcontains $r["tag"]) { [void]$newResults.Add($r) }
        }
        $s["results"] = @($newResults)
        foreach ($tag in $toRemove) {
            if ($drivers.ContainsKey($tag)) {
                $drivers.Remove($tag) | Out-Null
                $removed++
            }
        }
    }
    return $removed
}

function Infer-NextCompound($existingStints, $remainingLaps) {
    # F1 mandatory rule: must use at least 2 different dry compounds
    $usedVisuals = @{}
    foreach ($s in $existingStints) {
        $vid = $s["tyreVisualId"]
        if (-not $vid) { $vid = $s["tyreVisual"] }
        $usedVisuals[$vid] = $true
    }

    $usedM = $usedVisuals.ContainsKey($MEDIUM_VISUAL) -or $usedVisuals.ContainsKey("Medium")
    $usedH = $usedVisuals.ContainsKey($HARD_VISUAL) -or $usedVisuals.ContainsKey("Hard")
    $usedS = $usedVisuals.ContainsKey($SOFT_VISUAL) -or $usedVisuals.ContainsKey("Soft")

    $lastVisual = $null
    if ($existingStints.Count -gt 0) {
        $last = $existingStints[$existingStints.Count - 1]
        $lastVisual = $last["tyreVisualId"]
        if (-not $lastVisual) {
            if ($last["tyreVisual"] -eq "Medium") { $lastVisual = $MEDIUM_VISUAL }
            elseif ($last["tyreVisual"] -eq "Hard") { $lastVisual = $HARD_VISUAL }
            elseif ($last["tyreVisual"] -eq "Soft") { $lastVisual = $SOFT_VISUAL }
        }
    }

    # Strategy logic:
    # 1-stop: if all M -> switch to H; if all H -> switch to M
    # 2-stop: if M+M -> H; if M+H -> M; if H+M -> H
    # 3-stop: if M+M+M -> H; if mixed -> alternate
    # Long last stint (>8 laps) -> prefer Hard; Short (<8) -> could be any

    $onlyOneCompound = (!$usedM -or !$usedH) -and !$usedS
    if ($onlyOneCompound -and !$usedH -and $usedM) {
        # Only used Medium so far -> must switch to Hard (or Soft, but Hard is more common for long stints)
        return @{actual=$HARD_ACTUAL; visual=$HARD_VISUAL}
    }
    if ($onlyOneCompound -and $usedH -and !$usedM) {
        # Only used Hard -> must switch to Medium (or Soft)
        return @{actual=$MEDIUM_ACTUAL; visual=$MEDIUM_VISUAL}
    }

    # Already used 2+ compounds, so last stint is strategic choice
    # Prefer switching from current compound
    if ($lastVisual -eq $MEDIUM_VISUAL -or $lastVisual -eq "Medium") {
        return @{actual=$HARD_ACTUAL; visual=$HARD_VISUAL}
    }
    if ($lastVisual -eq $HARD_VISUAL -or $lastVisual -eq "Hard") {
        return @{actual=$MEDIUM_ACTUAL; visual=$MEDIUM_VISUAL}
    }

    # Default: Hard
    return @{actual=$HARD_ACTUAL; visual=$HARD_VISUAL}
}

function Detect-PitLaps($wearData) {
    $pitLaps = @()
    if (-not $wearData -or $wearData.Count -lt 2) { return $pitLaps }
    for ($i = 1; $i -lt $wearData.Count; $i++) {
        $prevAvg = $wearData[$i-1]["avg"]
        $currAvg = $wearData[$i]["avg"]
        if ($currAvg -lt ($prevAvg * 0.5)) {
            $pitLaps += $wearData[$i]["lapNumber"]
        }
    }
    return $pitLaps
}

$aiRemoved = Filter-AIDriversFromQuali $j
if ($aiRemoved -gt 0) { Write-Host "Filtered $aiRemoved AI drivers from qualifying" -ForegroundColor Magenta }

$sessions = $j["sessions"]
$fixCount = 0

foreach ($s in $sessions) {
    $st = $s["sessionType"]["name"]
    $drivers = $s["drivers"]
    if (-not $drivers) { continue }

    foreach ($dKey in @($drivers.Keys)) {
        $d = $drivers[$dKey]
        $stints = $d["tyreStints"]
        $pitStops = $d["pitStopsTimeline"]
        $wear = $d["tyreWearPerLap"]
        $laps = $d["laps"]

        $pitCount = if ($pitStops) { $pitStops.Count } else { 0 }
        $stintCount = if ($stints) { $stints.Count } else { 0 }
        $lapCount = if ($laps) { $laps.Count } else { 0 }

        if ($lapCount -eq 0) { continue }

        $wearPitLaps = Detect-PitLaps $wear
        $totalPits = [Math]::Max($pitCount, $wearPitLaps.Count)
        $expectedStints = $totalPits + 1

        if ($stintCount -ge $expectedStints) { continue }

        $maxLap = 0
        foreach ($l in $laps) { if ($l["lapNumber"] -gt $maxLap) { $maxLap = $l["lapNumber"] } }

        Write-Host ("  Fixing $dKey ($st): $stintCount -> $expectedStints stints (pits=$totalPits)") -ForegroundColor Yellow

        $newStints = [System.Collections.ArrayList]::new()
        if ($stints) { foreach ($ts in $stints) { [void]$newStints.Add($ts) } }

        $lastEndLap = 0
        if ($newStints.Count -gt 0) {
            $lastEndLap = [int]$newStints[$newStints.Count - 1]["endLap"]
        }

        while ($newStints.Count -lt $expectedStints) {
            $isLastStint = ($newStints.Count -eq $expectedStints - 1)
            $stintStartLap = $lastEndLap + 1
            if ($stintStartLap -le 0) { $stintStartLap = 1 }

            $remainingLaps = $maxLap - $stintStartLap + 1
            $compound = Infer-NextCompound $newStints $remainingLaps

            $endLap = 0
            if (-not $isLastStint) {
                $pitLaps = @()
                if ($pitStops) { foreach ($ps in $pitStops) { if ($ps["lapNum"]) { $pitLaps += [int]$ps["lapNum"] } } }
                if ($pitLaps.Count -eq 0) { $pitLaps = $wearPitLaps }
                $idx = $newStints.Count
                if ($idx -lt $pitLaps.Count) {
                    $endLap = [int]$pitLaps[$idx] - 2
                    if ($endLap -le $lastEndLap) { $endLap = $lastEndLap + 3 }
                } else {
                    $endLap = $lastEndLap + 5
                }
            }

            $newStint = @{
                "endLap" = $endLap
                "tyreActualId" = $compound.actual
                "tyreActual" = $tyreActualNames[$compound.actual]
                "tyreVisualId" = $compound.visual
                "tyreVisual" = $tyreVisualNames[$compound.visual]
            }

            $vis = $tyreVisualNames[$compound.visual]
            Write-Host "    + Stint $($newStints.Count + 1): $vis (endLap=$endLap)" -ForegroundColor DarkGreen
            [void]$newStints.Add($newStint)
            $lastEndLap = $endLap
            $fixCount++
        }

        $d["tyreStints"] = [object[]]$newStints
    }
}

Write-Host "`nTotal fixes: $fixCount" -ForegroundColor Cyan

$json = $ser.Serialize($j)
[System.IO.File]::WriteAllText($OutputPath, $json, [System.Text.Encoding]::UTF8)
Write-Host "Saved to: $OutputPath" -ForegroundColor Green
