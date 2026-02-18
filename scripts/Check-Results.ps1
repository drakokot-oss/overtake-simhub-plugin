[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue
$json = Get-Content "d:\Drako\Overtake\Overtake Telemetry\output\league_8526981694939562817_1771393856829.json" -Raw
$d = $ser.DeserializeObject($json)

$raceSession = $d['sessions'] | Where-Object { $_['sessionType']['name'] -eq 'Race' } | Select-Object -First 1
if ($raceSession) {
    Write-Host "=== RESULTS ===" -ForegroundColor Cyan
    foreach ($r in $raceSession['results']) {
        $grid = $r['grid']
        $gridStr = if ($grid -eq $null) { "NULL" } elseif ($grid -eq 0) { "ZERO" } else { $grid.ToString() }
        Write-Host "P$($r['position']) $($r['tag']) grid=$gridStr numLaps=$($r['numLaps']) bestLap=$($r['bestLapTime']) totalTime=$($r['totalTime']) status=$($r['status'])"
    }
    
    Write-Host ""
    Write-Host "=== FASTEST LAP CHECK ===" -ForegroundColor Cyan
    $ftlpEvents = $raceSession['events'] | Where-Object { $_['code'] -eq 'FTLP' }
    Write-Host "FTLP events: $($ftlpEvents.Count)"
    foreach ($e in $ftlpEvents) { 
        Write-Host "  FTLP: $($e | ConvertTo-Json -Compress)" 
    }

    $bestLaps = @()
    foreach ($r in $raceSession['results']) {
        if ($r['bestLapTimeMs'] -ne $null -and $r['bestLapTimeMs'] -gt 0) {
            $bestLaps += @{ tag = $r['tag']; ms = $r['bestLapTimeMs'] }
        }
    }
    $sorted = $bestLaps | Sort-Object { $_.ms }
    if ($sorted.Count -gt 0) {
        Write-Host "Best from results: $($sorted[0].tag) $($sorted[0].ms)ms"
    }
}
