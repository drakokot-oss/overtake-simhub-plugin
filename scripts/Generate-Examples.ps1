<#
.SYNOPSIS
    Generates 4 realistic example league-1.0 JSON files for testing.
#>
param([string]$OutDir = "")
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = (Resolve-Path "$PSScriptRoot\..\examples").Path }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function ToJson($obj) { return ($obj | ConvertTo-Json -Depth 20 -Compress) }

# ── Driver grid ──
$grid = @(
    @{tag="HAMILTON";   team=0; teamName="Mercedes-AMG Petronas";        num=44;  nat=12; did=0},
    @{tag="RUSSELL";    team=0; teamName="Mercedes-AMG Petronas";        num=63;  nat=12; did=1},
    @{tag="LECLERC";    team=1; teamName="Scuderia Ferrari HP";          num=16;  nat=78; did=9},
    @{tag="SAINZ";      team=1; teamName="Scuderia Ferrari HP";          num=55;  nat=38; did=2},
    @{tag="VERSTAPPEN"; team=2; teamName="Red Bull Racing";              num=1;   nat=75; did=17},
    @{tag="PEREZ";      team=2; teamName="Red Bull Racing";              num=11;  nat=70; did=3},
    @{tag="ALBON";      team=3; teamName="Williams Racing";              num=23;  nat=86; did=72},
    @{tag="SARGEANT";   team=3; teamName="Williams Racing";              num=2;   nat=0;  did=88},
    @{tag="ALONSO";     team=4; teamName="Aston Martin Aramco";          num=14;  nat=38; did=7},
    @{tag="STROLL";     team=4; teamName="Aston Martin Aramco";          num=18;  nat=52; did=33},
    @{tag="GASLY";      team=5; teamName="Alpine F1 Team";               num=10;  nat=78; did=36},
    @{tag="OCON";       team=5; teamName="Alpine F1 Team";               num=31;  nat=78; did=37},
    @{tag="TSUNODA";    team=6; teamName="Visa Cash App Racing Bulls";   num=22;  nat=49; did=67},
    @{tag="RICCIARDO";  team=6; teamName="Visa Cash App Racing Bulls";   num=3;   nat=3;  did=4},
    @{tag="MAGNUSSEN";  team=7; teamName="MoneyGram Haas F1 Team";       num=20;  nat=25; did=30},
    @{tag="HULKENBERG"; team=7; teamName="MoneyGram Haas F1 Team";       num=27;  nat=32; did=35},
    @{tag="NORRIS";     team=8; teamName="McLaren Formula 1 Team";       num=4;   nat=12; did=58},
    @{tag="PIASTRI";    team=8; teamName="McLaren Formula 1 Team";       num=81;  nat=3;  did=82},
    @{tag="BOTTAS";     team=9; teamName="Stake F1 Team Kick Sauber";    num=77;  nat=41; did=19},
    @{tag="ZHOU";       team=9; teamName="Stake F1 Team Kick Sauber";    num=24;  nat=56; did=83}
)

$rng = New-Object System.Random(42)

function FmtTime($ms) {
    $min = [int][math]::Floor($ms / 60000)
    $sec = [int][math]::Floor(($ms % 60000) / 1000)
    $mil = $ms % 1000
    return "{0}:{1:D2}.{2:D3}" -f $min, $sec, $mil
}

function MakeLaps($baseLapMs, $numLaps, $pitLap, $s1Pct, $s2Pct, $dnfLap) {
    $laps = @()
    for ($i = 1; $i -le $numLaps; $i++) {
        if ($dnfLap -gt 0 -and $i -gt $dnfLap) { break }
        $variation = $rng.Next(-800, 800)
        $lapMs = $baseLapMs + $variation
        if ($i -eq 1) { $lapMs += $rng.Next(2000, 5000) }
        if ($i -eq $pitLap) { $lapMs += $rng.Next(20000, 23000) }
        $s1 = [int]($lapMs * $s1Pct + $rng.Next(-200, 200))
        $s2 = [int]($lapMs * $s2Pct + $rng.Next(-200, 200))
        $s3 = $lapMs - $s1 - $s2
        $laps += @{
            lapNumber = $i; lapTimeMs = $lapMs; lapTime = (FmtTime $lapMs)
            sector1Ms = $s1; sector2Ms = $s2; sector3Ms = $s3
            valid = $true; flags = @("Valid"); tsMs = 0
        }
    }
    return $laps
}

function MakeWear($numLaps, $pitLap) {
    $wear = @()
    for ($i = 1; $i -le $numLaps; $i++) {
        $mult = if ($i -le $pitLap) { $i } else { $i - $pitLap }
        $base = $mult * 0.8 + ($rng.NextDouble() * 0.3)
        $wear += @{
            lapNumber=$i
            rl=[math]::Round($base + $rng.NextDouble()*0.5, 1)
            rr=[math]::Round($base + $rng.NextDouble()*0.6, 1)
            fl=[math]::Round($base * 0.7 + $rng.NextDouble()*0.3, 1)
            fr=[math]::Round($base * 0.75 + $rng.NextDouble()*0.3, 1)
            avg=[math]::Round($base * 0.85, 1)
        }
    }
    return $wear
}

function MakeDamage($numLaps, $dmgLap, $dmgWing, $dmgVal, $repairLap) {
    $dmg = @()
    for ($i = 1; $i -le $numLaps; $i++) {
        $d = @{lapNumber=$i; wingFL=0; wingFR=0; wingRear=0; tyreDmgRL=0; tyreDmgRR=0; tyreDmgFL=0; tyreDmgFR=0}
        if ($dmgLap -gt 0 -and $i -ge $dmgLap -and ($repairLap -eq 0 -or $i -lt $repairLap)) {
            $d[$dmgWing] = $dmgVal
        }
        $dmg += $d
    }
    return $dmg
}

function MakeStints($pitLap, $numLaps, $tyre1, $tyre2) {
    $tyreMap = @{ "Soft"=@(17,16); "Medium"=@(18,17); "Hard"=@(19,18); "Inter"=@(7,7); "Wet"=@(8,8) }
    $t1 = $tyreMap[$tyre1]; $t2 = $tyreMap[$tyre2]
    return @(
        @{endLap=$pitLap; tyreActualId=$t1[0]; tyreActual=$tyre1; tyreVisualId=$t1[1]; tyreVisual=$tyre1},
        @{endLap=$numLaps; tyreActualId=$t2[0]; tyreActual=$tyre2; tyreVisualId=$t2[1]; tyreVisual=$tyre2}
    )
}

function BuildSession($cfg) {
    $baseTs = $cfg.baseTs
    $numLaps = $cfg.numLaps
    $trackId = $cfg.trackId; $trackName = $cfg.trackName
    $sessionType = $cfg.sessionType; $sessionTypeId = $cfg.sessionTypeId
    $weatherId = $cfg.weatherId; $weatherName = $cfg.weatherName
    $trackTemp = $cfg.trackTemp; $airTemp = $cfg.airTemp
    $baseLapMs = $cfg.baseLapMs
    $s1Pct = $cfg.s1Pct; $s2Pct = $cfg.s2Pct
    $playerTag = $cfg.playerTag
    $scPeriods = if ($cfg.scPeriods) { $cfg.scPeriods } else { @() }
    $vscPeriods = if ($cfg.vscPeriods) { $cfg.vscPeriods } else { @() }
    $penalties = if ($cfg.penalties) { $cfg.penalties } else { @() }
    $collisions = if ($cfg.collisions) { $cfg.collisions } else { @() }
    $retirements = if ($cfg.retirements) { $cfg.retirements } else { @() }
    $wingDamages = if ($cfg.wingDamages) { $cfg.wingDamages } else { @() }
    $gridOrder = $cfg.gridOrder
    $finishOrder = $cfg.finishOrder
    $weatherChanges = if ($cfg.weatherChanges) { $cfg.weatherChanges } else { @() }
    $scDeploys = if ($cfg.scDeploys) { $cfg.scDeploys } else { 0 }
    $vscDeploys = if ($cfg.vscDeploys) { $cfg.vscDeploys } else { 0 }
    $redFlags = if ($cfg.redFlags) { $cfg.redFlags } else { 0 }

    $uid = "{0:D20}" -f ($rng.Next(1000000000) * [long]1000000000 + $rng.Next(1000000000))
    $lgotTs = $baseTs + 60000
    $chqfTs = $lgotTs + ($numLaps * $baseLapMs) + 30000
    $endTs = $chqfTs + 15000

    # Events
    $events = @()
    $events += @{type="EVENT"; code="SSTA"; tsMs=$baseTs}
    $events += @{type="EVENT"; code="LGOT"; tsMs=$lgotTs}

    foreach ($p in $penalties) {
        $penTs = $lgotTs + ($p.lap * $baseLapMs)
        $driverIdx = 0; for($x=0;$x-lt$grid.Count;$x++){if($grid[$x].tag -eq $p.tag){$driverIdx=$x;break}}
        $events += @{
            type="EVENT"; code="PENA"; tsMs=$penTs
            data=@{
                vehicleIdx=$driverIdx; vehicleTag=$p.tag
                penaltyType=$p.penaltyType; penaltyTypeName=$p.penaltyTypeName
                infringementType=$p.infringementType; infringementTypeName=$p.infringementTypeName
                otherVehicleIdx=255; timeSec=$p.timeSec; lapNum=$p.lap; placesGained=0
            }
        }
    }

    foreach ($c in $collisions) {
        $cTs = $lgotTs + ($c.lap * $baseLapMs)
        $idx1 = 0; $idx2 = 0
        for($x=0;$x-lt$grid.Count;$x++){if($grid[$x].tag -eq $c.driver1){$idx1=$x}; if($grid[$x].tag -eq $c.driver2){$idx2=$x}}
        $events += @{type="EVENT"; code="COLL"; tsMs=$cTs; data=@{vehicle1Idx=$idx1; vehicle2Idx=$idx2; vehicle1Tag=$c.driver1; vehicle2Tag=$c.driver2}}
    }

    foreach ($sc in $scPeriods) {
        $deployTs = $lgotTs + [int](($sc.startLap - 0.5) / $numLaps * ($chqfTs - $lgotTs))
        $endScTs = $lgotTs + [int](($sc.endLap + 0.5) / $numLaps * ($chqfTs - $lgotTs))
        $events += @{type="EVENT"; code="SCAR"; tsMs=$deployTs; data=@{safetyCarType=1; eventType=0}}
        $events += @{type="EVENT"; code="SCAR"; tsMs=$endScTs; data=@{safetyCarType=1; eventType=2}}
    }

    foreach ($vsc in $vscPeriods) {
        $deployTs = $lgotTs + [int](($vsc.startLap - 0.5) / $numLaps * ($chqfTs - $lgotTs))
        $endVscTs = $lgotTs + [int](($vsc.endLap + 0.5) / $numLaps * ($chqfTs - $lgotTs))
        $events += @{type="EVENT"; code="SCAR"; tsMs=$deployTs; data=@{safetyCarType=2; eventType=0}}
        $events += @{type="EVENT"; code="SCAR"; tsMs=$endVscTs; data=@{safetyCarType=2; eventType=2}}
    }

    foreach ($ret in $retirements) {
        $retTs = $lgotTs + ($ret.lap * $baseLapMs)
        $driverIdx = 0; for($x=0;$x-lt$grid.Count;$x++){if($grid[$x].tag -eq $ret.tag){$driverIdx=$x;break}}
        $events += @{type="EVENT"; code="RTMT"; tsMs=$retTs; data=@{vehicleIdx=$driverIdx; vehicleTag=$ret.tag; reason=$ret.reason}}
    }

    $ovtCount = $rng.Next(3,6)
    for ($o = 0; $o -lt $ovtCount; $o++) {
        $ovtLap = $rng.Next(2, $numLaps)
        $oi = $rng.Next(0, $grid.Count); $oj = ($oi + 1) % $grid.Count
        $events += @{type="EVENT"; code="OVTK"; tsMs=($lgotTs + $ovtLap * $baseLapMs); data=@{overtakerIdx=$oi; overtakenIdx=$oj; overtakerTag=$grid[$oi].tag; overtakenTag=$grid[$oj].tag}}
    }

    # Fastest lap
    $flIdx = 0; for($x=0;$x-lt$grid.Count;$x++){if($grid[$x].tag -eq $finishOrder[0]){$flIdx=$x;break}}
    $flTime = $baseLapMs - $rng.Next(500,1500)
    $events += @{type="EVENT"; code="FTLP"; tsMs=($chqfTs - 30000); data=@{vehicleIdx=$flIdx; vehicleTag=$finishOrder[0]; lapTimeSec=[math]::Round($flTime/1000.0,3)}}

    $events += @{type="EVENT"; code="CHQF"; tsMs=$chqfTs}
    $events += @{type="EVENT"; code="RCWN"; tsMs=($chqfTs+3000); data=@{vehicleIdx=$flIdx; vehicleTag=$finishOrder[0]}}
    $events += @{type="EVENT"; code="SEND"; tsMs=$endTs}

    $events = $events | Sort-Object { $_.tsMs }

    # Results + Drivers
    $resultsOut = @()
    $driversOut = [ordered]@{}
    $retiredTags = @{}; foreach($r in $retirements){$retiredTags[$r.tag] = $r.lap}
    $penMap = @{}; foreach($p in $penalties){if(-not $penMap[$p.tag]){$penMap[$p.tag]=@{count=0;timeSec=0;warns=0;cc=0}}; $penMap[$p.tag].count++; $penMap[$p.tag].timeSec += $p.timeSec; if($p.penaltyType -le 1){$penMap[$p.tag].warns++; $penMap[$p.tag].cc++}}
    $collMap = @{}; foreach($c in $collisions){if(-not $collMap[$c.driver1]){$collMap[$c.driver1]=@()}; $collMap[$c.driver1]+=@{tsMs=($lgotTs+$c.lap*$baseLapMs);type="collision"}; if(-not $collMap[$c.driver2]){$collMap[$c.driver2]=@()}; $collMap[$c.driver2]+=@{tsMs=($lgotTs+$c.lap*$baseLapMs);type="collision"}}
    $dmgMap = @{}; foreach($wd in $wingDamages){$dmgMap[$wd.tag] = $wd}

    $winnerTotalMs = 0

    for ($pos = 0; $pos -lt $finishOrder.Count; $pos++) {
        $tag = $finishOrder[$pos]
        $d = $null; for($x=0;$x-lt$grid.Count;$x++){if($grid[$x].tag -eq $tag){$d=$grid[$x];break}}
        $gridPos = 0; for($x=0;$x-lt$gridOrder.Count;$x++){if($gridOrder[$x] -eq $tag){$gridPos=$x+1;break}}

        $isRetired = $retiredTags.ContainsKey($tag)
        $dnfLap = if($isRetired){$retiredTags[$tag]}else{0}
        $actualLaps = if($isRetired){$dnfLap}else{$numLaps}
        $pitLap = if($isRetired -and $dnfLap -lt 5){0}else{$rng.Next(4, [math]::Min(8, $numLaps))}
        $speedOffset = $pos * $rng.Next(150, 350)
        $driverBaseLap = $baseLapMs + $speedOffset

        $laps = MakeLaps $driverBaseLap $numLaps $pitLap $s1Pct $s2Pct $dnfLap
        $lapTs = $lgotTs
        foreach ($l in $laps) { $lapTs += $l.lapTimeMs; $l.tsMs = $lapTs }

        $totalMs = 0; foreach($l in $laps){$totalMs += $l.lapTimeMs}
        if ($pos -eq 0) { $winnerTotalMs = $totalMs }

        $bestLap = $laps | Sort-Object lapTimeMs | Select-Object -First 1
        $bestS1 = $laps | Sort-Object sector1Ms | Select-Object -First 1
        $bestS2 = $laps | Sort-Object sector2Ms | Select-Object -First 1
        $bestS3 = $laps | Sort-Object sector3Ms | Select-Object -First 1

        $pen = if($penMap[$tag]){$penMap[$tag]}else{@{count=0;timeSec=0;warns=0;cc=0}}
        $status = if($isRetired){"DidNotFinish"}else{"Finished"}
        $tyre1 = if($cfg.startTyre){$cfg.startTyre}else{"Soft"}
        $tyre2 = if($cfg.endTyre){$cfg.endTyre}else{"Hard"}

        $wd = $dmgMap[$tag]
        $dmgLap = if($wd){$wd.lap}else{0}
        $dmgWing = if($wd){$wd.wing}else{"wingFR"}
        $dmgVal = if($wd){$wd.value}else{0}
        $repairLap = if($wd -and $wd.repairLap){$wd.repairLap}else{0}

        $repairs = @()
        if ($wd -and $repairLap -gt 0) {
            $repairs += @{lap=$repairLap; wing=$dmgWing; damageBefore=$dmgVal; damageAfter=0; repaired=$dmgVal}
        }

        $penTimeline = @()
        foreach ($p in $penalties) {
            if ($p.tag -eq $tag) {
                $cat = "penalty"
                if ($p.penaltyType -le 1) { $cat = "warning" }
                $penTimeline += @{
                    tsMs=($lgotTs + $p.lap * $baseLapMs); category=$cat
                    penaltyType=$p.penaltyType; penaltyTypeName=$p.penaltyTypeName
                    infringementType=$p.infringementType; infringementTypeName=$p.infringementTypeName
                    otherDriver=""; timeSec=$p.timeSec; lapNum=$p.lap
                }
            }
        }

        $resultsOut += @{
            position=($pos+1); tag=$tag; teamId=$d.team; teamName=$d.teamName
            grid=$gridPos; numLaps=$actualLaps
            bestLapTimeMs=$bestLap.lapTimeMs; bestLapTime=$bestLap.lapTime
            totalTimeMs=$totalMs; totalTime=(FmtTime $totalMs)
            penaltiesTimeSec=$pen.timeSec; pitStops=([int]($pitLap -gt 0))
            status=$status; numPenalties=$pen.count
        }

        $driversOut[$tag] = @{
            position=$gridPos; teamId=$d.team; teamName=$d.teamName
            myTeam=$false; raceNumber=$d.num; aiControlled=$false
            isPlayer=($tag -eq $playerTag); platform="Steam"
            showOnlineNames=$true; yourTelemetry="public"; nationality=$d.nat
            laps=$laps
            tyreStints=(MakeStints $pitLap $actualLaps $tyre1 $tyre2)
            tyreWearPerLap=(MakeWear $actualLaps $pitLap)
            damagePerLap=(MakeDamage $actualLaps $dmgLap $dmgWing $dmgVal $repairLap)
            wingRepairs=$repairs
            best=@{
                bestLapTimeLapNum=$bestLap.lapNumber; bestLapTimeMs=$bestLap.lapTimeMs
                bestSector1LapNum=$bestS1.lapNumber; bestSector1Ms=$bestS1.sector1Ms
                bestSector2LapNum=$bestS2.lapNumber; bestSector2Ms=$bestS2.sector2Ms
                bestSector3LapNum=$bestS3.lapNumber; bestSector3Ms=$bestS3.sector3Ms
            }
            pitStopsTimeline=@(if($pitLap -gt 0){@{numPitStops=1; tsMs=($lgotTs + $pitLap * $baseLapMs); lapNum=$pitLap}}else{})
            penaltiesTimeline=$penTimeline
            collisionsTimeline=@(if($collMap[$tag]){$collMap[$tag]}else{})
            totalWarnings=$pen.warns; cornerCuttingWarnings=$pen.cc
        }
    }

    # lapsUnderSC / lapsUnderVSC
    $scLaps = @(); $vscLaps = @()
    foreach ($sc in $scPeriods) { for($l=$sc.startLap;$l-le$sc.endLap;$l++){$scLaps+=$l} }
    foreach ($vsc in $vscPeriods) { for($l=$vsc.startLap;$l-le$vsc.endLap;$l++){$vscLaps+=$l} }

    # Awards
    $finishedResults = $resultsOut | Where-Object {$_.status -eq "Finished"}
    $flResult = $finishedResults | Sort-Object bestLapTimeMs | Select-Object -First 1

    $consistentCandidates = @()
    $topHalf = [int][math]::Ceiling($finishedResults.Count / 2)
    $eligibleTags = ($finishedResults | Select-Object -First $topHalf).tag
    foreach ($et in $eligibleTags) {
        $dLaps = $driversOut[$et].laps | Where-Object {$_.lapNumber -gt 1}
        if ($dLaps.Count -ge 5) {
            $times = $dLaps | ForEach-Object {$_.lapTimeMs}
            $avg = ($times | Measure-Object -Average).Average
            $variance = ($times | ForEach-Object {[math]::Pow($_ - $avg, 2)} | Measure-Object -Average).Average
            $stdDev = [math]::Round([math]::Sqrt($variance), 0)
            $consistentCandidates += @{tag=$et; stdDevMs=$stdDev; cleanLaps=$dLaps.Count}
        }
    }
    $mostConsistent = $consistentCandidates | Sort-Object stdDevMs | Select-Object -First 1

    $posGained = @()
    foreach ($fr in $finishedResults) {
        $gained = $fr.grid - $fr.position
        if ($gained -gt 0) { $posGained += @{tag=$fr.tag; grid=$fr.grid; finish=$fr.position; gained=$gained} }
    }
    $mostPosGained = $posGained | Sort-Object {-$_.gained}, {$_.finish} | Select-Object -First 1

    $awards = @{
        fastestLap = @{tag=$flResult.tag; timeMs=$flResult.bestLapTimeMs; time=$flResult.bestLapTime}
        mostConsistent = if($mostConsistent){@{tag=$mostConsistent.tag; stdDevMs=$mostConsistent.stdDevMs; stdDev=("{0:F3}" -f ($mostConsistent.stdDevMs/1000.0)); cleanLaps=$mostConsistent.cleanLaps}}else{$null}
        mostPositionsGained = if($mostPosGained){$mostPosGained}else{$null}
    }

    $wTimeline = @(@{tsMs=$baseTs; weather=@{id=$weatherId;name=$weatherName}; trackTempC=$trackTemp; airTempC=$airTemp})
    foreach ($wc in $weatherChanges) {
        $wTimeline += @{tsMs=($lgotTs + [int]($wc.lap / $numLaps * ($chqfTs - $lgotTs))); weather=@{id=$wc.id;name=$wc.name}; trackTempC=$wc.trackTemp; airTempC=$wc.airTemp}
    }

    $scStatus = "NoSafetyCar"
    if ($scDeploys -gt 0) { $scStatus = "FullSafetyCar" }
    elseif ($vscDeploys -gt 0) { $scStatus = "VirtualSafetyCar" }

    return @{
        sessionUID=$uid
        sessionType=@{id=$sessionTypeId; name=$sessionType}
        track=@{id=$trackId; name=$trackName}
        weather=@{id=$weatherId; name=$weatherName}
        trackTempC=$trackTemp; airTempC=$airTemp
        weatherTimeline=$wTimeline
        weatherForecast=@(
            @{timeOffsetMin=0; weather=@{id=$weatherId;name=$weatherName}; trackTempC=$trackTemp; airTempC=$airTemp; rainPercentage=0},
            @{timeOffsetMin=5; weather=@{id=$weatherId;name=$weatherName}; trackTempC=$trackTemp; airTempC=$airTemp; rainPercentage=0},
            @{timeOffsetMin=15; weather=@{id=$weatherId;name=$weatherName}; trackTempC=$trackTemp; airTempC=$airTemp; rainPercentage=0}
        )
        lastPacketMs=$endTs; sessionEndedAtMs=$endTs
        safetyCar=@{status=@{id=0;name=$scStatus}; fullDeploys=$scDeploys; vscDeploys=$vscDeploys; redFlagPeriods=$redFlags; lapsUnderSC=$scLaps; lapsUnderVSC=$vscLaps}
        networkGame=$true
        awards=$awards; results=$resultsOut; drivers=$driversOut; events=$events
    }
}

function BuildQualifying($cfg, $gridOrder) {
    $uid = "{0:D20}" -f ($rng.Next(1000000000) * [long]1000000000 + $rng.Next(1000000000))
    $qBaseTs = $cfg.baseTs - 600000

    $resultsOut = @()
    $driversOut = [ordered]@{}
    $events = @(@{type="EVENT"; code="SSTA"; tsMs=$qBaseTs}, @{type="EVENT"; code="SEND"; tsMs=($qBaseTs+540000)})

    for ($pos = 0; $pos -lt $gridOrder.Count; $pos++) {
        $tag = $gridOrder[$pos]
        $d = $null; for($x=0;$x-lt$grid.Count;$x++){if($grid[$x].tag -eq $tag){$d=$grid[$x];break}}
        $qLapMs = $cfg.baseLapMs - $rng.Next(1500, 3500) + ($pos * $rng.Next(80, 250))

        $s1 = [int]($qLapMs * $cfg.s1Pct + $rng.Next(-150, 150))
        $s2 = [int]($qLapMs * $cfg.s2Pct + $rng.Next(-150, 150))
        $s3 = $qLapMs - $s1 - $s2
        $lap = @{lapNumber=1; lapTimeMs=$qLapMs; lapTime=(FmtTime $qLapMs); sector1Ms=$s1; sector2Ms=$s2; sector3Ms=$s3; valid=$true; flags=@("Valid"); tsMs=($qBaseTs + 120000 + $pos * 15000)}

        $resultsOut += @{
            position=($pos+1); tag=$tag; teamId=$d.team; teamName=$d.teamName
            grid=$null; numLaps=1; bestLapTimeMs=$qLapMs; bestLapTime=(FmtTime $qLapMs)
            totalTimeMs=$qLapMs; totalTime=(FmtTime $qLapMs)
            penaltiesTimeSec=0; pitStops=0; status="Finished"; numPenalties=0
        }
        $driversOut[$tag] = @{
            position=0; teamId=$d.team; teamName=$d.teamName
            myTeam=$false; raceNumber=$d.num; aiControlled=$false
            isPlayer=($tag -eq $cfg.playerTag); platform="Steam"
            showOnlineNames=$true; yourTelemetry="public"; nationality=$d.nat
            laps=@($lap)
            tyreStints=@(@{endLap=255; tyreActualId=17; tyreActual="Soft"; tyreVisualId=16; tyreVisual="Soft"})
            tyreWearPerLap=@(@{lapNumber=1;rl=0.4;rr=0.5;fl=0.2;fr=0.3;avg=0.35})
            damagePerLap=@(@{lapNumber=1;wingFL=0;wingFR=0;wingRear=0;tyreDmgRL=0;tyreDmgRR=0;tyreDmgFL=0;tyreDmgFR=0})
            wingRepairs=@(); best=@{bestLapTimeLapNum=1;bestLapTimeMs=$qLapMs;bestSector1LapNum=1;bestSector1Ms=$s1;bestSector2LapNum=1;bestSector2Ms=$s2;bestSector3LapNum=1;bestSector3Ms=$s3}
            pitStopsTimeline=@(); penaltiesTimeline=@(); collisionsTimeline=@()
            totalWarnings=0; cornerCuttingWarnings=0
        }
    }

    return @{
        sessionUID=$uid
        sessionType=@{id=12; name="OneShotQualifying"}
        track=@{id=$cfg.trackId; name=$cfg.trackName}
        weather=@{id=$cfg.weatherId; name=$cfg.weatherName}
        trackTempC=$cfg.trackTemp; airTempC=$cfg.airTemp
        weatherTimeline=@(@{tsMs=$qBaseTs; weather=@{id=$cfg.weatherId;name=$cfg.weatherName}; trackTempC=$cfg.trackTemp; airTempC=$cfg.airTemp})
        weatherForecast=@(@{timeOffsetMin=0; weather=@{id=$cfg.weatherId;name=$cfg.weatherName}; trackTempC=$cfg.trackTemp; airTempC=$cfg.airTemp; rainPercentage=0})
        lastPacketMs=($qBaseTs+540000); sessionEndedAtMs=($qBaseTs+540000)
        safetyCar=@{status=@{id=0;name="NoSafetyCar"}; fullDeploys=0; vscDeploys=0; redFlagPeriods=0; lapsUnderSC=@(); lapsUnderVSC=@()}
        networkGame=$true
        awards=@{fastestLap=$null; mostConsistent=$null; mostPositionsGained=$null}
        results=$resultsOut; drivers=$driversOut; events=$events
    }
}

function BuildFullJson($cfg) {
    $quali = BuildQualifying $cfg $cfg.gridOrder
    $race = BuildSession $cfg

    $allTags = @()
    foreach ($d in $grid) { $allTags += $d.tag }

    return @{
        schemaVersion = "league-1.0"
        game = "F1_25"
        capture = @{
            sessionUID = $race.sessionUID
            startedAtMs = $cfg.baseTs - 600000
            endedAtMs = $race.sessionEndedAtMs
            source = @{}
            sessionTypesInCapture = @("OneShotQualifying", $cfg.sessionType)
        }
        participants = $allTags
        sessions = @($quali, $race)
        _debug = @{packets=@{total=85000}; plugin=@{version="1.1.0"}}
    }
}

# ══════════════════════════════════════════
# RACE 1: Monza — Dry, Clean, No SC
# ══════════════════════════════════════════
Write-Host "Generating Race 1: Monza (dry, clean)..." -ForegroundColor Cyan
$rng = New-Object System.Random(101)
$monzaGrid = @("VERSTAPPEN","LECLERC","NORRIS","SAINZ","PIASTRI","HAMILTON","RUSSELL","ALONSO","GASLY","OCON","TSUNODA","RICCIARDO","PEREZ","HULKENBERG","MAGNUSSEN","ALBON","STROLL","BOTTAS","SARGEANT","ZHOU")
$monzaFinish = @("VERSTAPPEN","LECLERC","NORRIS","PIASTRI","HAMILTON","SAINZ","RUSSELL","ALONSO","GASLY","PEREZ","OCON","TSUNODA","RICCIARDO","HULKENBERG","MAGNUSSEN","ALBON","STROLL","BOTTAS","SARGEANT","ZHOU")

$monza = BuildFullJson @{
    baseTs=1771275600000; numLaps=10; trackId=11; trackName="Monza"
    sessionType="Race"; sessionTypeId=10
    weatherId=0; weatherName="Clear"; trackTemp=32; airTemp=26
    baseLapMs=81500; s1Pct=0.34; s2Pct=0.35
    playerTag="LECLERC"; gridOrder=$monzaGrid; finishOrder=$monzaFinish
    startTyre="Soft"; endTyre="Hard"
    penalties=@(
        @{tag="NORRIS";  lap=5; penaltyType=0; penaltyTypeName="Warning"; infringementType=7; infringementTypeName="CornerCuttingGainedTime"; timeSec=0},
        @{tag="STROLL";  lap=8; penaltyType=4; penaltyTypeName="TimePenalty"; infringementType=7; infringementTypeName="CornerCuttingGainedTime"; timeSec=5}
    )
    collisions=@(@{driver1="MAGNUSSEN"; driver2="ALBON"; lap=3})
    retirements=@(@{tag="ZHOU"; lap=7; reason=4})
}
$monzaJson = ToJson $monza
Set-Content -Path "$OutDir\Monza_20260215_200000_A1B2C3.json" -Value $monzaJson -Encoding UTF8
Write-Host "  OK" -ForegroundColor Green

# ══════════════════════════════════════════
# RACE 2: Spa — Rain, Full SC + VSC, Wing Damage
# ══════════════════════════════════════════
Write-Host "Generating Race 2: Spa (rain, SC + VSC, wing damage)..." -ForegroundColor Cyan
$rng = New-Object System.Random(202)
$spaGrid = @("NORRIS","VERSTAPPEN","LECLERC","HAMILTON","PIASTRI","SAINZ","RUSSELL","GASLY","ALONSO","OCON","TSUNODA","PEREZ","RICCIARDO","HULKENBERG","MAGNUSSEN","ALBON","STROLL","BOTTAS","SARGEANT","ZHOU")
$spaFinish = @("VERSTAPPEN","NORRIS","LECLERC","HAMILTON","PIASTRI","RUSSELL","SAINZ","ALONSO","GASLY","PEREZ","OCON","HULKENBERG","RICCIARDO","MAGNUSSEN","ALBON","STROLL","BOTTAS","SARGEANT","TSUNODA","ZHOU")

$spa = BuildFullJson @{
    baseTs=1771362000000; numLaps=12; trackId=10; trackName="Spa"
    sessionType="Race"; sessionTypeId=10
    weatherId=2; weatherName="Overcast"; trackTemp=18; airTemp=15
    baseLapMs=107000; s1Pct=0.33; s2Pct=0.39
    playerTag="VERSTAPPEN"; gridOrder=$spaGrid; finishOrder=$spaFinish
    startTyre="Medium"; endTyre="Inter"
    scPeriods=@(@{startLap=5; endLap=7})
    vscPeriods=@(@{startLap=10; endLap=11})
    scDeploys=1; vscDeploys=1
    weatherChanges=@(
        @{lap=4; id=3; name="LightRain"; trackTemp=16; airTemp=14},
        @{lap=8; id=4; name="HeavyRain"; trackTemp=14; airTemp=12}
    )
    penalties=@(
        @{tag="PEREZ";   lap=3;  penaltyType=4; penaltyTypeName="TimePenalty"; infringementType=2; infringementTypeName="CausedCollision"; timeSec=5},
        @{tag="MAGNUSSEN"; lap=6; penaltyType=0; penaltyTypeName="Warning"; infringementType=7; infringementTypeName="CornerCuttingGainedTime"; timeSec=0}
    )
    collisions=@(
        @{driver1="PEREZ"; driver2="TSUNODA"; lap=3},
        @{driver1="ZHOU"; driver2="SARGEANT"; lap=5}
    )
    retirements=@(
        @{tag="TSUNODA"; lap=4; reason=3},
        @{tag="ZHOU"; lap=5; reason=6}
    )
    wingDamages=@(
        @{tag="STROLL"; lap=3; wing="wingFR"; value=45; repairLap=6}
    )
}
$spaJson = ToJson $spa
Set-Content -Path "$OutDir\Spa_20260216_150000_D4E5F6.json" -Value $spaJson -Encoding UTF8
Write-Host "  OK" -ForegroundColor Green

# ══════════════════════════════════════════
# RACE 3: Monaco — Dry→Rain, Red Flag, Chaos
# ══════════════════════════════════════════
Write-Host "Generating Race 3: Monaco (dry to rain, red flag, chaos)..." -ForegroundColor Cyan
$rng = New-Object System.Random(303)
$monacoGrid = @("LECLERC","SAINZ","VERSTAPPEN","NORRIS","HAMILTON","PIASTRI","RUSSELL","ALONSO","GASLY","OCON","PEREZ","TSUNODA","RICCIARDO","HULKENBERG","MAGNUSSEN","ALBON","STROLL","BOTTAS","SARGEANT","ZHOU")
$monacoFinish = @("LECLERC","VERSTAPPEN","HAMILTON","NORRIS","SAINZ","RUSSELL","ALONSO","GASLY","PIASTRI","OCON","PEREZ","RICCIARDO","HULKENBERG","ALBON","BOTTAS","STROLL","SARGEANT","TSUNODA","ZHOU","MAGNUSSEN")

$monaco = BuildFullJson @{
    baseTs=1771448400000; numLaps=10; trackId=5; trackName="Monaco"
    sessionType="Race"; sessionTypeId=10
    weatherId=0; weatherName="Clear"; trackTemp=28; airTemp=22
    baseLapMs=75500; s1Pct=0.35; s2Pct=0.38
    playerTag="LECLERC"; gridOrder=$monacoGrid; finishOrder=$monacoFinish
    startTyre="Soft"; endTyre="Medium"
    scPeriods=@(@{startLap=4; endLap=6})
    scDeploys=1; redFlags=1
    weatherChanges=@(@{lap=7; id=3; name="LightRain"; trackTemp=22; airTemp=19})
    penalties=@(
        @{tag="MAGNUSSEN"; lap=3; penaltyType=4; penaltyTypeName="TimePenalty"; infringementType=2; infringementTypeName="CausedCollision"; timeSec=10},
        @{tag="PEREZ";     lap=5; penaltyType=0; penaltyTypeName="Warning"; infringementType=7; infringementTypeName="CornerCuttingGainedTime"; timeSec=0},
        @{tag="TSUNODA";   lap=4; penaltyType=2; penaltyTypeName="DriveThrough"; infringementType=6; infringementTypeName="DrivingTooSlow"; timeSec=0},
        @{tag="STROLL";    lap=8; penaltyType=4; penaltyTypeName="TimePenalty"; infringementType=0; infringementTypeName="BlockingBySlowDriving"; timeSec=5}
    )
    collisions=@(
        @{driver1="MAGNUSSEN"; driver2="TSUNODA"; lap=3},
        @{driver1="ZHOU"; driver2="SARGEANT"; lap=4},
        @{driver1="STROLL"; driver2="BOTTAS"; lap=6},
        @{driver1="PEREZ"; driver2="RICCIARDO"; lap=5},
        @{driver1="MAGNUSSEN"; driver2="ALBON"; lap=8}
    )
    retirements=@(
        @{tag="TSUNODA"; lap=4; reason=6},
        @{tag="ZHOU";    lap=4; reason=6},
        @{tag="MAGNUSSEN"; lap=9; reason=3}
    )
    wingDamages=@(
        @{tag="MAGNUSSEN"; lap=3; wing="wingFL"; value=62; repairLap=0},
        @{tag="STROLL";    lap=6; wing="wingFR"; value=35; repairLap=8}
    )
}
$monacoJson = ToJson $monaco
Set-Content -Path "$OutDir\Monaco_20260217_180000_G7H8I9.json" -Value $monacoJson -Encoding UTF8
Write-Host "  OK" -ForegroundColor Green

# ══════════════════════════════════════════
# RACE 4: Jeddah — Night, Multiple VSC, DT + Stop-Go
# ══════════════════════════════════════════
Write-Host "Generating Race 4: Jeddah (night, VSCs, DT + stop-go penalties)..." -ForegroundColor Cyan
$rng = New-Object System.Random(404)
$jeddahGrid = @("HAMILTON","RUSSELL","VERSTAPPEN","LECLERC","NORRIS","PIASTRI","SAINZ","ALONSO","GASLY","OCON","TSUNODA","PEREZ","RICCIARDO","HULKENBERG","MAGNUSSEN","ALBON","STROLL","BOTTAS","SARGEANT","ZHOU")
$jeddahFinish = @("RUSSELL","VERSTAPPEN","LECLERC","NORRIS","HAMILTON","PIASTRI","SAINZ","ALONSO","GASLY","OCON","PEREZ","TSUNODA","RICCIARDO","HULKENBERG","MAGNUSSEN","ALBON","STROLL","BOTTAS","SARGEANT","ZHOU")

$jeddah = BuildFullJson @{
    baseTs=1771534800000; numLaps=12; trackId=29; trackName="Jeddah"
    sessionType="Race"; sessionTypeId=10
    weatherId=0; weatherName="Clear"; trackTemp=26; airTemp=24
    baseLapMs=90500; s1Pct=0.31; s2Pct=0.37
    playerTag="HAMILTON"; gridOrder=$jeddahGrid; finishOrder=$jeddahFinish
    startTyre="Soft"; endTyre="Hard"
    vscPeriods=@(
        @{startLap=3; endLap=4},
        @{startLap=9; endLap=10}
    )
    vscDeploys=2
    penalties=@(
        @{tag="MAGNUSSEN"; lap=2;  penaltyType=2; penaltyTypeName="DriveThrough"; infringementType=2; infringementTypeName="CausedCollision"; timeSec=0},
        @{tag="STROLL";    lap=5;  penaltyType=3; penaltyTypeName="StopGo"; infringementType=1; infringementTypeName="FalseStart"; timeSec=10},
        @{tag="PEREZ";     lap=7;  penaltyType=4; penaltyTypeName="TimePenalty"; infringementType=7; infringementTypeName="CornerCuttingGainedTime"; timeSec=5},
        @{tag="RICCIARDO"; lap=10; penaltyType=0; penaltyTypeName="Warning"; infringementType=7; infringementTypeName="CornerCuttingGainedTime"; timeSec=0}
    )
    collisions=@(
        @{driver1="MAGNUSSEN"; driver2="BOTTAS"; lap=2},
        @{driver1="STROLL"; driver2="ALBON"; lap=5},
        @{driver1="PEREZ"; driver2="GASLY"; lap=9}
    )
    retirements=@()
    wingDamages=@(
        @{tag="BOTTAS"; lap=2; wing="wingRear"; value=38; repairLap=5},
        @{tag="ALBON";  lap=5; wing="wingFL"; value=28; repairLap=7}
    )
}
$jeddahJson = ToJson $jeddah
Set-Content -Path "$OutDir\Jeddah_20260218_210000_J1K2L3.json" -Value $jeddahJson -Encoding UTF8
Write-Host "  OK" -ForegroundColor Green

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  4 example JSONs generated in:" -ForegroundColor Green
Write-Host "  $OutDir" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Green
Get-ChildItem $OutDir -Filter "*.json" | ForEach-Object {
    Write-Host "  $($_.Name)  ($([math]::Round($_.Length/1024, 1)) KB)" -ForegroundColor Gray
}
Write-Host ""
