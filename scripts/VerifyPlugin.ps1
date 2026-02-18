Add-Type -Assembly "System.IO.Compression.FileSystem"
$pkg = "d:\Drako\Overtake\overtake-simhub-plugin\dist\OvertakeTelemetry-20260218.simhubplugin"
if (-not (Test-Path $pkg)) {
    Write-Host "Package not found: $pkg" -ForegroundColor Red
    exit 1
}
$zip = [System.IO.Compression.ZipFile]::OpenRead($pkg)
Write-Host "Contents of .simhubplugin:"
foreach ($entry in $zip.Entries) {
    Write-Host "  $($entry.FullName)  ($([math]::Round($entry.Length/1024, 1)) KB)"
}
$zip.Dispose()
Write-Host ""
Write-Host "Package size: $([math]::Round((Get-Item $pkg).Length/1024, 1)) KB"
