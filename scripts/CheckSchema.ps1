[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 50MB
$j = $ser.DeserializeObject([System.IO.File]::ReadAllText("d:\Drako\Overtake\Overtake Telemetry\output\Suzuka_20260218_220813_484091.json"))

Write-Host "=== Top-level keys ===" -ForegroundColor Cyan
$j.Keys | ForEach-Object { Write-Host "  $_" }

Write-Host "`n=== Capture keys ===" -ForegroundColor Cyan
$j["capture"].Keys | ForEach-Object { Write-Host "  $_" }

$sessions = $j["sessions"]
Write-Host "`n=== Sessions: $($sessions.Count) ===" -ForegroundColor Cyan

foreach ($s in $sessions) {
    $st = $s["sessionType"]["name"]
    Write-Host "`n--- Session: $st ---" -ForegroundColor Yellow
    Write-Host "Session-level keys:" -ForegroundColor Green
    $s.Keys | ForEach-Object { Write-Host "  $_" }

    if ($s["results"] -and $s["results"].Count -gt 0) {
        Write-Host "Results[0] keys:" -ForegroundColor Green
        $s["results"][0].Keys | ForEach-Object { Write-Host "  $_" }
    }

    $drivers = $s["drivers"]
    if ($drivers -and $drivers.Keys.Count -gt 0) {
        $firstKey = ($drivers.Keys | Select-Object -First 1)
        $firstDriver = $drivers[$firstKey]
        Write-Host "Driver[$firstKey] keys:" -ForegroundColor Green
        $firstDriver.Keys | ForEach-Object { Write-Host "  $_" }

        if ($firstDriver["laps"] -and $firstDriver["laps"].Count -gt 0) {
            Write-Host "Driver.laps[0] keys:" -ForegroundColor Green
            $firstDriver["laps"][0].Keys | ForEach-Object { Write-Host "  $_" }
        }

        if ($firstDriver["tyreStints"] -and $firstDriver["tyreStints"].Count -gt 0) {
            Write-Host "Driver.tyreStints[0] keys:" -ForegroundColor Green
            $firstDriver["tyreStints"][0].Keys | ForEach-Object { Write-Host "  $_" }
        }

        if ($firstDriver["pitStopsTimeline"] -and $firstDriver["pitStopsTimeline"].Count -gt 0) {
            Write-Host "Driver.pitStopsTimeline[0] keys:" -ForegroundColor Green
            $firstDriver["pitStopsTimeline"][0].Keys | ForEach-Object { Write-Host "  $_" }
        }

        if ($firstDriver["tyreWearPerLap"] -and $firstDriver["tyreWearPerLap"].Count -gt 0) {
            Write-Host "Driver.tyreWearPerLap[0] keys:" -ForegroundColor Green
            $firstDriver["tyreWearPerLap"][0].Keys | ForEach-Object { Write-Host "  $_" }
        }

        if ($firstDriver["damagePerLap"] -and $firstDriver["damagePerLap"].Count -gt 0) {
            Write-Host "Driver.damagePerLap[0] keys:" -ForegroundColor Green
            $firstDriver["damagePerLap"][0].Keys | ForEach-Object { Write-Host "  $_" }
        }
    }

    if ($s["safetyCar"]) {
        Write-Host "SafetyCar keys:" -ForegroundColor Green
        $s["safetyCar"].Keys | ForEach-Object { Write-Host "  $_" }
    }

    if ($s["awards"]) {
        Write-Host "Awards keys:" -ForegroundColor Green
        $s["awards"].Keys | ForEach-Object { Write-Host "  $_" }
    }
}
