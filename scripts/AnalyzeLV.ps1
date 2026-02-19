$jsonPath = "d:\Drako\Overtake\Overtake Telemetry\output\LasVegas_20260218_205020_38ADB6.json"
[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 50MB
$raw = [System.IO.File]::ReadAllText($jsonPath)
$j = $ser.DeserializeObject($raw)

Write-Host "=== PARTICIPANTS ===" -ForegroundColor Cyan
$parts = $j["participants"]
for ($i = 0; $i -lt $parts.Count; $i++) { Write-Host "  [$i] $($parts[$i])" }

Write-Host "`n=== SESSIONS ===" -ForegroundColor Cyan
$sessions = $j["sessions"]
Write-Host "  Total sessions: $($sessions.Count)"
foreach ($s in $sessions) {
    $st = $s["sessionType"]
    $tr = $s["track"]
    $results = $s["results"]
    $drivers = $s["drivers"]
    Write-Host "`n  Session: $($st["name"]) @ $($tr["name"])" -ForegroundColor Yellow
    Write-Host "  Results count: $(if($results){$results.Count}else{'N/A'})"
    Write-Host "  Drivers count: $(if($drivers){$drivers.Count}else{'N/A'})"
    
    if ($results) {
        Write-Host "`n  --- RESULTS ---" -ForegroundColor Green
        foreach ($r in $results) {
            $tag = $r["tag"]
            $pos = $r["position"]
            $grid = $r["grid"]
            $team = $r["teamName"]
            $laps = $r["numLaps"]
            $status = $r["resultStatus"]
            $isPlayer = $r["isPlayer"]
            Write-Host ("  P{0,2} | grid:{1,3} | {2,-20} | {3,-30} | laps:{4} | {5} | player:{6}" -f $pos, $(if($grid){"$grid"}else{"null"}), $tag, $team, $laps, $status, $isPlayer)
        }
    }
    
    if ($drivers) {
        Write-Host "`n  --- DRIVERS DETAIL ---" -ForegroundColor Green
        foreach ($dKey in $drivers.Keys) {
            $d = $drivers[$dKey]
            $lapCount = 0
            if ($d["laps"]) { $lapCount = $d["laps"].Count }
            $tyreStints = 0
            if ($d["tyreStints"]) { $tyreStints = $d["tyreStints"].Count }
            $ai = $d["aiControlled"]
            $ip = $d["isPlayer"]
            $team = $d["teamName"]
            $pits = 0
            if ($d["pitStops"]) { $pits = $d["pitStops"].Count }
            Write-Host ("  {0,-20} | team:{1,-30} | laps:{2,2} | stints:{3} | pits:{4} | ai:{5} | player:{6}" -f $dKey, $team, $lapCount, $tyreStints, $pits, $ai, $ip)
        }
    }
}

Write-Host "`n=== DEBUG SECTION ===" -ForegroundColor Cyan
$dbg = $j["_debug"]
if ($dbg) {
    $sh = $dbg["sessionHistory"]
    $ld = $dbg["lapData"]
    $pa = $dbg["participants"]
    Write-Host "  SessionHistory:" -ForegroundColor Yellow
    if ($sh) { foreach ($k in $sh.Keys) { Write-Host "    $k = $($sh[$k])" } }
    Write-Host "  LapData:" -ForegroundColor Yellow  
    if ($ld) { foreach ($k in $ld.Keys) { Write-Host "    $k = $($ld[$k])" } }
    Write-Host "  Participants:" -ForegroundColor Yellow
    if ($pa) { foreach ($k in $pa.Keys) { Write-Host "    $k = $($pa[$k])" } }
}

Write-Host "`n=== ANALYSIS ===" -ForegroundColor Magenta
$placeholders = @($parts | Where-Object { $_ -match "^Player_\d+$" })
$driverGeneric = @($parts | Where-Object { $_ -match "^Driver_\d+$" })
$realNames = @($parts | Where-Object { $_ -notmatch "^Player_\d+$" -and $_ -notmatch "^Driver_\d+$" })
Write-Host "  Player_X placeholders: $($placeholders.Count) -> $($placeholders -join ', ')"
Write-Host "  Driver_X generics:     $($driverGeneric.Count) -> $($driverGeneric -join ', ')"
Write-Host "  Real names:            $($realNames.Count) -> $($realNames -join ', ')"
