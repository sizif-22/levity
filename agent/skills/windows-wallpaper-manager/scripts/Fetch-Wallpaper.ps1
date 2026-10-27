param(
    [Parameter(Mandatory = $false, Position = 0)]
    [string]$Query = "black and white minimalist",

    [Parameter(Mandatory = $false)]
    [string]$AtLeast = "1920x1080",

    [Parameter(Mandatory = $false)]
    [string]$Ratio = "16x9",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "$env:USERPROFILE\Pictures\Wallpapers",

    [Parameter(Mandatory = $false)]
    [int]$Count = 1,

    [Parameter(Mandatory = $false)]
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$encodedQuery = [uri]::EscapeDataString($Query)
$apiUrl = "https://wallhaven.cc/api/v1/search?q=$encodedQuery&categories=100&purity=100&sorting=favorites&ratios=$Ratio&atleast=$AtLeast"

$headers = @{ "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" }
$response = Invoke-RestMethod -Uri $apiUrl -Headers $headers

if (-not $response.data -or $response.data.Count -eq 0) {
    # Fallback to general search without ratio restriction
    $fallbackUrl = "https://wallhaven.cc/api/v1/search?q=$encodedQuery&categories=100&purity=100&sorting=views"
    $response = Invoke-RestMethod -Uri $fallbackUrl -Headers $headers
}

if (-not $response.data -or $response.data.Count -eq 0) {
    throw "No wallpapers found matching query: $Query"
}

$items = $response.data | Select-Object -First $Count
$downloadedFiles = @()

foreach ($item in $items) {
    $ext = [System.IO.Path]::GetExtension($item.path)
    if (-not $ext) { $ext = ".jpg" }
    
    $cleanQuery = ($Query -replace '[^a-zA-Z0-9]', '-').Trim('-').ToLower()
    $fileName = "$cleanQuery-$($item.id)-$($item.resolution)$ext"
    $targetPath = Join-Path $OutputDir $fileName

    if (-not (Test-Path $targetPath)) {
        Write-Output "Downloading $($item.resolution) wallpaper ($($item.id))..."
        Invoke-WebRequest -Uri $item.path -OutFile $targetPath -Headers $headers
    } else {
        Write-Output "File already exists: $targetPath"
    }

    $downloadedFiles += $targetPath
}

if ($Apply -and $downloadedFiles.Count -gt 0) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $setterPath = Join-Path $scriptDir "Set-Wallpaper.ps1"
    & $setterPath -Path $downloadedFiles[0]
}

$downloadedFiles | ForEach-Object { Write-Output "Ready: $_" }
