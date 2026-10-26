# Creates Desktop and Start Menu shortcuts for Levity so it shows up in Windows Search.
$root = $PSScriptRoot
$target = Join-Path $root "publish\Levity.exe"
$icon = Join-Path $root "Assets\app.ico"

if (-not (Test-Path $target)) {
    Write-Host "Levity.exe not found. Build it first: dotnet publish -c Release -r win-x64 --self-contained false -o publish" -ForegroundColor Red
    exit 1
}

$shell = New-Object -ComObject WScript.Shell
$locations = @(
    [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Desktop),
    (Join-Path ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Programs)) "")
)

foreach ($dir in $locations) {
    $path = Join-Path $dir "Levity.lnk"
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = Split-Path $target
    $lnk.IconLocation = "$icon, 0"
    $lnk.Description = "Levity - Antigravity CLI sidebar and mobile control"
    $lnk.Save()
    Write-Host "Created $path" -ForegroundColor Green
}
