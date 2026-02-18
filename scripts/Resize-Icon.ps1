Add-Type -AssemblyName System.Drawing

$srcPath = "C:\Users\w\.cursor\projects\d-Drako-Overtake-f125-telemetry-mvp\assets\overtake-icon-v3.png"
$destPath = "d:\Drako\Overtake\overtake-simhub-plugin\src\Overtake.SimHub.Plugin\Assets\overtake-icon.png"

$src = [System.Drawing.Image]::FromFile($srcPath)
Write-Host "Source: $($src.Width)x$($src.Height)"

$size = 64
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$g.DrawImage($src, 0, 0, $size, $size)
$bmp.Save($destPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
$src.Dispose()

$fi = Get-Item $destPath
Write-Host "Saved: $($fi.Length) bytes ($size x $size)"
