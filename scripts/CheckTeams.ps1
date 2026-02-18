$json = Get-Content "d:\Drako\Overtake\Overtake Telemetry\output\Spa_20260218_152737_BB20D0.json" -Raw | ConvertFrom-Json

foreach ($sess in $json.sessions) {
    if ($sess.sessionType.name -eq "SessionType(None)") { continue }
    Write-Host ("=== {0} ===" -f $sess.sessionType.name) -ForegroundColor Cyan
    
    # Drivers
    $dps = $sess.drivers | Get-Member -MemberType NoteProperty
    Write-Host "  Drivers:" -ForegroundColor Yellow
    foreach ($dp in $dps) {
        $d = $sess.drivers.($dp.Name)
        $teamStr = if ($d.teamId -ne $null -and $d.teamId -ne "") { $d.teamId } else { "NO_TEAM_ID" }
        $teamNameStr = if ($d.teamName) { $d.teamName } else { "NO_TEAM_NAME" }
        Write-Host ("    {0,-20} teamId={1,-4} teamName={2}" -f $dp.Name, $teamStr, $teamNameStr) 
    }
    
    # Results
    Write-Host "  Results:" -ForegroundColor Yellow
    foreach ($r in $sess.results) {
        $teamStr = if ($r.teamName) { $r.teamName } else { "NO_TEAM_NAME" }
        $teamIdStr = if ($r.teamId -ne $null -and $r.teamId -ne "") { $r.teamId } else { "NO_TEAM_ID" }
        Write-Host ("    P{0,-2} {1,-20} teamId={2,-4} teamName={3}" -f $r.position, $r.tag, $teamIdStr, $teamStr)
    }
}
