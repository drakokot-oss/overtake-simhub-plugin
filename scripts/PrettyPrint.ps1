$inPath = "d:\Drako\Overtake\Overtake Telemetry\output\Suzuka_20260218_220813_484091_FIXED.json"
$raw = [System.IO.File]::ReadAllText($inPath)
$obj = $raw | ConvertFrom-Json
$pretty = $obj | ConvertTo-Json -Depth 30
[System.IO.File]::WriteAllText($inPath, $pretty, [System.Text.Encoding]::UTF8)
Write-Host "Pretty-printed OK - $($pretty.Length) chars"
