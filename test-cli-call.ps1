# 清理缓存
$cachePath = Join-Path $env:LOCALAPPDATA "CCUsage\ccusage.db"
if (Test-Path $cachePath) { Remove-Item $cachePath -Force; Write-Host "Cache cleared" }

Write-Host "Running App for 10 seconds..."
$proc = Start-Process -FilePath "E:\dev\cholf5\ccusage-gui\src\CCUsage.App\bin\Debug\net8.0\CCUsage.App.exe" -PassThru
Start-Sleep 10
if (!$proc.HasExited) { Stop-Process -Id $proc.Id -Force }
Write-Host "App closed"

Start-Sleep 2

if (Test-Path $cachePath) {
    Write-Host "Cache file exists: $((Get-Item $cachePath).Length) bytes" -ForegroundColor Green
} else {
    Write-Host "No cache file created" -ForegroundColor Red
}
