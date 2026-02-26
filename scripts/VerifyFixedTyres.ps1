[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 50MB
$j = $ser.DeserializeObject([System.IO.File]::ReadAllText("d:\Drako\Overtake\Overtake Telemetry\output\Suzuka_20260218_220813_484091_FIXED.json"))

$sessions = $j["sessions"]
foreach ($s in $sessions) {
    $st = $s["sessionType"]["name"]
    if ($st -ne "Race") { continue }
    Write-Host "=== Session: $st ===" -ForegroundColor Cyan
    $drivers = $s["drivers"]

    foreach ($dKey in $drivers.Keys) {
        $d = $drivers[$dKey]
        $stints = $d["tyreStints"]
        $lapCount = if ($d["laps"]) { $d["laps"].Count } else { 0 }
        $pitStops = $d["pitStopsTimeline"]
        $pitCount = if ($pitStops) { $pitStops.Count } else { 0 }

        if ($lapCount -eq 0) { continue }

        $stintCount = if ($stints) { $stints.Count } else { 0 }
        $expected = $pitCount + 1
        $status = if ($stintCount -ge $expected) { "OK" } else { "INCOMPLETE" }
        $color = if ($status -eq "OK") { "Green" } else { "Red" }

        Write-Host ("  $dKey [$status] laps:$lapCount pits:$pitCount stints:$stintCount (expected:$expected)") -ForegroundColor $color

        if ($stints) {
            $stintStr = @()
            foreach ($ts in $stints) {
                $vis = $ts["tyreVisual"]
                $el = $ts["endLap"]
                $stintStr += "$vis(endLap=$el)"
            }
            Write-Host ("    Strategy: " + ($stintStr -join " -> "))
        }
    }
}
