$ErrorActionPreference = "Stop"
$dllPath = "D:\Drako\Overtake\overtake-simhub-plugin\src\Overtake.SimHub.Plugin\bin\Release\Overtake.SimHub.Plugin.dll"
$asm = [System.Reflection.Assembly]::LoadFrom($dllPath)
$writerType = $asm.GetType("Overtake.SimHub.Plugin.Security.OtkWriter")
$bf = [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic
$readMethod = $writerType.GetMethod("ReadOtk", $bf)
$writeMethod = $writerType.GetMethod("WriteOtk", $bf)

# Test: C# reads Python-generated .otk
$pyOtk = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "py_generated.otk")
if (Test-Path $pyOtk) {
    $decrypted = $readMethod.Invoke($null, @($pyOtk))
    if ($decrypted -match "league-1.0") {
        Write-Host "CROSS-TEST 1 PASS: C# reads Python .otk" -ForegroundColor Green
    } else {
        Write-Host "CROSS-TEST 1 FAIL" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "Python .otk not found" -ForegroundColor Red
    exit 1
}

# Generate C# .otk for Python to read
$csOtk = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "cs_generated.otk")
$testJson = '{"schema":"league-1.0","sessions":[{"type":"Race","track":"Monza"}],"participants":[{"tag":"TestDriver","team":"Mercedes"}]}'
$writeMethod.Invoke($null, @($testJson, $csOtk))
Write-Host "C# .otk saved to: $csOtk"
Write-Host "CROSS-TEST 2: Run Python to read C# .otk" -ForegroundColor Yellow
