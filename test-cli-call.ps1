# 清理缓存
$cachePath = Join-Path $env:LOCALAPPDATA "Tokenmetry\tokenmetry.db"
if (Test-Path $cachePath) { Remove-Item $cachePath -Force; Write-Host "Cache cleared" }

Write-Host "Running App for 10 seconds..."
$proc = Start-Process -FilePath "E:\dev\cholf5\ccusage\ccusage-gui\src\Tokenmetry.App\bin\Debug\net8.0\Tokenmetry.App.exe" -PassThru
Start-Sleep 10
if (!$proc.HasExited) { Stop-Process -Id $proc.Id -Force }
Write-Host "App closed"

Start-Sleep 2

if (Test-Path $cachePath) {
    Write-Host "Cache file exists: $((Get-Item $cachePath).Length) bytes" -ForegroundColor Green
} else {
    Write-Host "No cache file created" -ForegroundColor Red
}
