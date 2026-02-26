$jsonPath = "d:\Drako\Overtake\Overtake Telemetry\output\Suzuka_20260218_220813_484091.json"
[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 50MB
$raw = [System.IO.File]::ReadAllText($jsonPath)
$j = $ser.DeserializeObject($raw)

Write-Host "=== PARTICIPANTS ===" -ForegroundColor Cyan
$parts = $j["participants"]
for ($i = 0; $i -lt $parts.Count; $i++) { Write-Host "  [$i] $($parts[$i])" }

$sessions = $j["sessions"]
Write-Host "`n=== SESSIONS ($($sessions.Count)) ===" -ForegroundColor Cyan

foreach ($s in $sessions) {
    $st = $s["sessionType"]
    $tr = $s["track"]
    $uid = $s["sessionUID"]
    Write-Host "`n--- Session: $($st["name"]) (id:$($st["id"])) @ $($tr["name"]) UID:$uid ---" -ForegroundColor Yellow
    
    $results = $s["results"]
    $drivers = $s["drivers"]
    Write-Host "  Results: $(if($results){$results.Count}else{'N/A'})  Drivers: $(if($drivers){$drivers.Count}else{'N/A'})"
    
    if ($results) {
        Write-Host "`n  RESULTS:" -ForegroundColor Green
        foreach ($r in $results) {
            $tag = $r["tag"]
            $pos = $r["position"]
            $grid = $r["grid"]
            $team = $r["teamName"]
            $laps = $r["numLaps"]
            $best = $r["bestLapTime"]
            $status = $r["status"]
            $isP = $r["isPlayer"]
            Write-Host ("  P{0,2} | grid:{1,3} | {2,-25} | {3,-30} | laps:{4} | best:{5} | {6}" -f $pos, $(if($grid){"$grid"}else{"null"}), $tag, $team, $laps, $best, $status)
        }
    }
    
    if ($drivers) {
        Write-Host "`n  DRIVERS:" -ForegroundColor Green
        foreach ($dKey in $drivers.Keys) {
            $d = $drivers[$dKey]
            $lapCount = 0
            if ($d["laps"]) { $lapCount = $d["laps"].Count }
            $ai = $d["aiControlled"]
            $team = $d["teamName"]
            $stints = 0
            if ($d["tyreStints"]) { $stints = $d["tyreStints"].Count }
            Write-Host ("  {0,-25} | team:{1,-30} | laps:{2,2} | stints:{3} | ai:{4}" -f $dKey, $team, $lapCount, $stints, $ai)
        }
    }
}

Write-Host "`n=== DEBUG ===" -ForegroundColor Cyan
$dbg = $j["_debug"]
if ($dbg) {
    $diag = $dbg["diagnostics"]
    if ($diag) {
        $sh = $diag["sessionHistory"]
        $ld = $diag["lapData"]
        $pa = $diag["participants"]
        if ($sh) { Write-Host "  SessionHistory: received=$($sh["received"]) noDriver=$($sh["noDriver"]) earlyReg=$($sh["earlyRegister"])" }
        if ($ld) { Write-Host "  LapData: recorded=$($ld["lapRecorded"]) noDriver=$($ld["noDriver"]) earlyReg=$($ld["earlyRegister"])" }
        if ($pa) { Write-Host "  Participants: received=$($pa["received"]) numActive=$($pa["numActive"]) playerIdx=$($pa["playerCarIdx"]) overflow=$($pa["playerRecoveredFromOverflow"])" }
    }
}

# Identify Player # names
Write-Host "`n=== PLAYER # ANALYSIS ===" -ForegroundColor Magenta
$playerHash = @($parts | Where-Object { $_ -match "^Player #\d+$" -or $_ -match "^Player_\d+$" -or $_ -eq "Player" })
$driverGeneric = @($parts | Where-Object { $_ -match "^Driver_\d+$" })
$carGeneric = @($parts | Where-Object { $_ -match "^Car_\d+$" })
$realNames = @($parts | Where-Object { $_ -notmatch "^Player[_ #]" -and $_ -ne "Player" -and $_ -notmatch "^Driver_\d+$" -and $_ -notmatch "^Car_\d+$" })
Write-Host "  Player #/Player_X: $($playerHash.Count) -> $($playerHash -join ', ')"
Write-Host "  Driver_X:          $($driverGeneric.Count) -> $($driverGeneric -join ', ')"
Write-Host "  Car_X:             $($carGeneric.Count) -> $($carGeneric -join ', ')"
Write-Host "  Real names:        $($realNames.Count) -> $($realNames -join ', ')"

# Check for AI driver names in quali
Write-Host "`n=== AI DRIVER CHECK ===" -ForegroundColor Magenta
$knownAI = @("LECLERC","GASLY","HAMILTON","VERSTAPPEN","NORRIS","PIASTRI","SAINZ","RUSSELL","PEREZ","ALONSO","STROLL","OCON","TSUNODA","RICCIARDO","BOTTAS","ZHOU","MAGNUSSEN","HULKENBERG","ALBON","SARGEANT","BEARMAN","LAWSON","COLAPINTO","DOOHAN","ANTONELLI","HADJAR","BORTOLETO")
foreach ($s in $sessions) {
    $st = $s["sessionType"]["name"]
    $drivers = $s["drivers"]
    if (-not $drivers) { continue }
    $aiFound = @()
    foreach ($dKey in $drivers.Keys) {
        $upper = $dKey.ToUpper()
        foreach ($ai in $knownAI) {
            if ($upper -eq $ai) { $aiFound += $dKey; break }
        }
    }
    if ($aiFound.Count -gt 0) {
        Write-Host "  Session $st has F1 AI driver names: $($aiFound -join ', ')" -ForegroundColor Red
    } else {
        Write-Host "  Session $st - no AI driver names found" -ForegroundColor Green
    }
}
