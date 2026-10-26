$WshShell = New-Object -ComObject WScript.Shell
$DesktopPath = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Desktop)
$ShortcutPath = Join-Path $DesktopPath "Levity.lnk"
$TargetPath = "C:\Users\sheri\source\repos\AntigravitySidebar\publish\Levity.exe"
$IconPath = "C:\Users\sheri\source\repos\AntigravitySidebar\Assets\app.ico"

$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = $TargetPath
$Shortcut.WorkingDirectory = "C:\Users\sheri\source\repos\AntigravitySidebar\publish"
$Shortcut.IconLocation = "$IconPath, 0"
$Shortcut.Description = "Levity - Antigravity CLI sidebar and mobile control"
$Shortcut.Save()

Write-Host "Desktop shortcut created successfully at: $ShortcutPath" -ForegroundColor Green
