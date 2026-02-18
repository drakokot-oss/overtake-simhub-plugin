Add-Type -AssemblyName System.Drawing

$src = "C:\Users\w\.cursor\projects\d-Drako-Overtake-f125-telemetry-mvp\assets\c__Users_w_AppData_Roaming_Cursor_User_workspaceStorage_3817de9c778e8b3d87eaaf2b9ad9f466_images_overtake_logo_transparent_64x64-61b5ae47-a320-4a22-9957-81e8fc2b65b6.png"
$dst = "d:\Drako\Overtake\overtake-simhub-plugin\src\Overtake.SimHub.Plugin\Assets\overtake-icon.png"

$img = [System.Drawing.Image]::FromFile($src)
Write-Host "Source: $($img.Width)x$($img.Height)"

if ($img.Width -ne 64 -or $img.Height -ne 64) {
    $bmp = New-Object System.Drawing.Bitmap(64, 64)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($img, 0, 0, 64, 64)
    $g.Dispose()
    $img.Dispose()
    $bmp.Save($dst, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Resized and saved to 64x64"
} else {
    $img.Dispose()
    Copy-Item $src $dst -Force
    Write-Host "Already 64x64, copied directly"
}

$info = Get-Item $dst
Write-Host "Output: $($info.Length) bytes"
