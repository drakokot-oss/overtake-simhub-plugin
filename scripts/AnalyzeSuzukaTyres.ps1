$jsonPath = "d:\Drako\Overtake\Overtake Telemetry\output\Suzuka_20260218_220813_484091.json"
[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 50MB
$j = $ser.DeserializeObject([System.IO.File]::ReadAllText($jsonPath))

$sessions = $j["sessions"]
foreach ($s in $sessions) {
    $st = $s["sessionType"]["name"]
    Write-Host "=== Session: $st ===" -ForegroundColor Cyan
    $drivers = $s["drivers"]
    if (-not $drivers) { continue }
    
    foreach ($dKey in $drivers.Keys) {
        $d = $drivers[$dKey]
        $stints = $d["tyreStints"]
        $lapCount = if ($d["laps"]) { $d["laps"].Count } else { 0 }
        $pitStops = $d["pitStopsTimeline"]
        $pitCount = if ($pitStops) { $pitStops.Count } else { 0 }
        
        Write-Host "`n  $dKey (laps:$lapCount, pits:$pitCount)" -ForegroundColor Yellow
        
        if ($stints -and $stints.Count -gt 0) {
            Write-Host "    tyreStints ($($stints.Count)):" -ForegroundColor Green
            foreach ($ts in $stints) {
                Write-Host ("      endLap={0} actual={1}(id:{2}) visual={3}(id:{4})" -f $ts["endLap"], $ts["tyreActual"], $ts["tyreActualId"], $ts["tyreVisual"], $ts["tyreVisualId"])
            }
        } else {
            Write-Host "    tyreStints: EMPTY" -ForegroundColor Red
        }
        
        if ($pitStops -and $pitStops.Count -gt 0) {
            Write-Host "    pitStops:" -ForegroundColor Green
            foreach ($ps in $pitStops) {
                Write-Host ("      pit #{0} at lap {1}" -f $ps["numPitStops"], $ps["lapNum"])
            }
        }
        
        # Check wear drop (pit stop detection)
        $twpl = $d["tyreWearPerLap"]
        if ($twpl -and $twpl.Count -gt 1) {
            $wearDrops = @()
            for ($i = 1; $i -lt $twpl.Count; $i++) {
                $prevAvg = $twpl[$i-1]["avg"]
                $currAvg = $twpl[$i]["avg"]
                if ($currAvg -lt $prevAvg) {
                    $wearDrops += "lap $($twpl[$i]["lapNumber"]): $([math]::Round($prevAvg,1))% -> $([math]::Round($currAvg,1))%"
                }
            }
            if ($wearDrops.Count -gt 0) {
                Write-Host "    Wear drops (pit stops detected):" -ForegroundColor Magenta
                foreach ($wd in $wearDrops) { Write-Host "      $wd" }
            }
        }
    }
}
