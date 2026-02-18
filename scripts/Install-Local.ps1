$ErrorActionPreference = "Stop"
$dllSrc = "D:\Drako\Overtake\overtake-simhub-plugin\dist\Overtake.SimHub.Plugin.dll"
$candidates = @(
    "${env:ProgramFiles(x86)}\SimHub",
    "$env:ProgramFiles\SimHub",
    "D:\Program Files (x86)\SimHub",
    "D:\Program Files\SimHub",
    "D:\SimHub",
    "C:\SimHub"
)
$shPath = $null
foreach ($c in $candidates) {
    if (Test-Path "$c\SimHubWPF.exe") { $shPath = $c; break }
}
if (-not $shPath) {
    Write-Host "SimHub not found in standard locations" -ForegroundColor Red
    exit 1
}
Write-Host "SimHub found at: $shPath" -ForegroundColor Green
$dest = "$shPath\Overtake.SimHub.Plugin.dll"
Copy-Item $dllSrc $dest -Force
Write-Host "DLL installed to: $dest" -ForegroundColor Green
$size = [math]::Round((Get-Item $dest).Length / 1024, 1)
Write-Host "Size: $size KB" -ForegroundColor Gray
