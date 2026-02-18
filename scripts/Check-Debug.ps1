[void][System.Reflection.Assembly]::LoadWithPartialName("System.Web.Extensions")
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue
$json = Get-Content "d:\Drako\Overtake\Overtake Telemetry\output\league_8526981694939562817_1771393856829.json" -Raw
$d = $ser.DeserializeObject($json)

$dbg = $d['_debug']
Write-Host "=== DEBUG ===" -ForegroundColor Cyan
Write-Host "Packet ID counts:"
foreach ($k in $dbg['packetIdCounts'].Keys | Sort-Object) {
    Write-Host "  Packet $k : $($dbg['packetIdCounts'][$k])"
}

Write-Host ""
$diag = $dbg['diagnostics']
Write-Host "=== DIAGNOSTICS ===" -ForegroundColor Yellow
foreach ($section in $diag.Keys) {
    Write-Host "  Section: $section" -ForegroundColor Cyan
    $sub = $diag[$section]
    if ($sub -is [System.Collections.IDictionary]) {
        foreach ($k in $sub.Keys) {
            Write-Host "    $k = $($sub[$k])"
        }
    } else {
        Write-Host "    $sub"
    }
}

# Check ALL sessions for TagsByCarIdx
Write-Host ""
Write-Host "=== ALL SESSION TAGS ===" -ForegroundColor Yellow
foreach ($s in $d['sessions']) {
    $st = $s['sessionType']
    Write-Host "Session $($st['name']) ($($s['sessionUID'])):" -ForegroundColor Cyan
    $drvKeys = @()
    if ($s['drivers'] -ne $null) {
        $drvKeys = @($s['drivers'].Keys)
    }
    Write-Host "  Drivers: $($drvKeys.Count)"
    Write-Host "  Driver tags: $($drvKeys -join ', ')"
    
    $resCount = 0
    if ($s['results'] -ne $null) {
        $resCount = $s['results'].Count
    }
    Write-Host "  Results: $resCount"
    if ($resCount -gt 0) {
        $resTags = @()
        foreach ($r in $s['results']) { $resTags += $r['tag'] }
        Write-Host "  Result tags: $($resTags -join ', ')"
    }
}

# Check participants
Write-Host ""
Write-Host "=== PARTICIPANTS ===" -ForegroundColor Yellow
Write-Host "Total: $($d['participants'].Count)"
Write-Host "Tags: $($d['participants'] -join ', ')"

# Check for any driver-like names that could be the player
$allDriverTags = @()
foreach ($s in $d['sessions']) {
    if ($s['drivers'] -ne $null) {
        foreach ($k in $s['drivers'].Keys) {
            if ($k -notin $allDriverTags) { $allDriverTags += $k }
        }
    }
}
Write-Host ""
Write-Host "=== ALL UNIQUE DRIVER TAGS ACROSS ALL SESSIONS ===" -ForegroundColor Yellow
Write-Host $($allDriverTags -join ', ')

# Check for "Driver_" or "Player_" tags
$suspiciousTags = $allDriverTags | Where-Object { $_ -match 'Driver_|Player_|Car\d|^[a-z]' }
if ($suspiciousTags.Count -gt 0) {
    Write-Host ""
    Write-Host "=== SUSPICIOUS TAGS (possible player) ===" -ForegroundColor Red
    Write-Host $($suspiciousTags -join ', ')
}
