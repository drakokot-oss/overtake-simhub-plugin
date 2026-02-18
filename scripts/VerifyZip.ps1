Add-Type -Assembly "System.IO.Compression.FileSystem"
$zipPath = Get-ChildItem "d:\Drako\Overtake\overtake-simhub-plugin\dist\OvertakeTelemetry-v*.zip" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
Write-Host "Contents of OvertakeTelemetry-v1.0.0.zip:"
Write-Host ""
foreach ($entry in $zip.Entries) {
    $sizeKB = [math]::Round($entry.Length / 1024, 1)
    Write-Host "  $($entry.FullName)  ($sizeKB KB)"
}
$zip.Dispose()
Write-Host ""
$totalKB = [math]::Round((Get-Item $zipPath).Length / 1024, 1)
Write-Host "Total ZIP size: $totalKB KB"
