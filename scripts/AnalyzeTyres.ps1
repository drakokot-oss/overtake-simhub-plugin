param([string]$JsonPath = "d:\Drako\Overtake\Overtake Telemetry\output\LasVegas_20260218_205020_38ADB6.json")
[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 50MB
$raw = [System.IO.File]::ReadAllText($JsonPath)
$j = $ser.DeserializeObject($raw)

$sessions = $j["sessions"]
foreach ($s in $sessions) {
    $st = $s["sessionType"]
    Write-Host "=== Session: $($st["name"]) ===" -ForegroundColor Cyan
    
    $drivers = $s["drivers"]
    if (-not $drivers) { Write-Host "  No drivers section"; continue }
    
    foreach ($dKey in $drivers.Keys) {
        $d = $drivers[$dKey]
        Write-Host "`n  Driver: $dKey" -ForegroundColor Yellow
        
        # Tyre Stints
        $stints = $d["tyreStints"]
        if ($stints -and $stints.Count -gt 0) {
            Write-Host "    tyreStints ($($stints.Count)):" -ForegroundColor Green
            foreach ($ts in $stints) {
                $endLap = $ts["endLap"]
                $tyreActual = $ts["tyreActual"]
                $tyreActualId = $ts["tyreActualId"]
                $tyreVisual = $ts["tyreVisual"]
                $tyreVisualId = $ts["tyreVisualId"]
                Write-Host ("      stint: endLap={0}, actual={1}(id:{2}), visual={3}(id:{4})" -f $endLap, $tyreActual, $tyreActualId, $tyreVisual, $tyreVisualId)
            }
        } else {
            Write-Host "    tyreStints: EMPTY" -ForegroundColor Red
        }
        
        # Tyre Wear Per Lap
        $twpl = $d["tyreWearPerLap"]
        if ($twpl -and $twpl.Count -gt 0) {
            Write-Host "    tyreWearPerLap ($($twpl.Count) laps):" -ForegroundColor Green
            foreach ($tw in $twpl) {
                $lap = $tw["lapNumber"]
                $rl = $tw["rl"]; $rr = $tw["rr"]; $fl = $tw["fl"]; $fr = $tw["fr"]; $avg = $tw["avg"]
                Write-Host ("      lap {0}: RL={1:P1} RR={2:P1} FL={3:P1} FR={4:P1} avg={5:P1}" -f $lap, $rl, $rr, $fl, $fr, $avg)
            }
        } else {
            Write-Host "    tyreWearPerLap: EMPTY" -ForegroundColor Red
        }
        
        # Damage Per Lap
        $dmg = $d["damagePerLap"]
        if ($dmg -and $dmg.Count -gt 0) {
            Write-Host "    damagePerLap ($($dmg.Count) laps):" -ForegroundColor Green
            foreach ($dp in $dmg) {
                $lap = $dp["lapNumber"]
                $wfl = $dp["wingFL"]; $wfr = $dp["wingFR"]; $wr = $dp["wingRear"]
                $tdrl = $dp["tyreDmgRL"]; $tdrr = $dp["tyreDmgRR"]; $tdfl = $dp["tyreDmgFL"]; $tdfr = $dp["tyreDmgFR"]
                Write-Host ("      lap {0}: wingFL={1} wingFR={2} wingR={3} tyreDmg={4}/{5}/{6}/{7}" -f $lap, $wfl, $wfr, $wr, $tdrl, $tdrr, $tdfl, $tdfr)
            }
        } else {
            Write-Host "    damagePerLap: EMPTY" -ForegroundColor Red
        }
    }
}

# Summary
Write-Host "`n=== SUMMARY ===" -ForegroundColor Magenta
$totalDrivers = 0; $withStints = 0; $withWear = 0; $withDamage = 0
foreach ($s in $sessions) {
    $drivers = $s["drivers"]
    if (-not $drivers) { continue }
    foreach ($dKey in $drivers.Keys) {
        $d = $drivers[$dKey]
        $totalDrivers++
        if ($d["tyreStints"] -and $d["tyreStints"].Count -gt 0) { $withStints++ }
        if ($d["tyreWearPerLap"] -and $d["tyreWearPerLap"].Count -gt 0) { $withWear++ }
        if ($d["damagePerLap"] -and $d["damagePerLap"].Count -gt 0) { $withDamage++ }
    }
}
Write-Host "  Total drivers: $totalDrivers"
Write-Host "  With tyreStints:     $withStints / $totalDrivers" -ForegroundColor $(if($withStints -gt 0){"Green"}else{"Red"})
Write-Host "  With tyreWearPerLap: $withWear / $totalDrivers" -ForegroundColor $(if($withWear -gt 0){"Green"}else{"Red"})
Write-Host "  With damagePerLap:   $withDamage / $totalDrivers" -ForegroundColor $(if($withDamage -gt 0){"Green"}else{"Red"})
