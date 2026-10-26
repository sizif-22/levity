$exePath = Join-Path $PSScriptRoot "publish\Levity.exe"
Start-Process -FilePath $exePath
Write-Host "Levity started! Check your Windows taskbar system tray or press Win+Alt+A." -ForegroundColor Cyan
