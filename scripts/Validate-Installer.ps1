$f = "d:\Drako\Overtake\overtake-simhub-plugin\dist\Install-OvertakeTelemetry.bat"
$code = Get-Content $f -Raw
$errs = $null
[System.Management.Automation.PSParser]::Tokenize($code, [ref]$errs) | Out-Null
Write-Host "Parse errors: $($errs.Count)"
foreach ($e in $errs) {
    Write-Host "  Line $($e.Token.StartLine): $($e.Message)" -ForegroundColor Red
}
if ($errs.Count -eq 0) {
    Write-Host "Script is valid PowerShell." -ForegroundColor Green
}
