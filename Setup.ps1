<#
.SYNOPSIS
  One-step setup for Levity on a fresh Windows machine.

.DESCRIPTION
  1. Checks the tools Levity needs (.NET 8 SDK, agy; Node only for the phone app).
  2. Creates .env from .env.example if you do not have one.
  3. Builds the desktop app into .\publish.
  4. Optionally installs the agent skills and creates Desktop / Start Menu shortcuts.

.PARAMETER Skills
  Also run Install-Skills.ps1.

.PARAMETER Shortcuts
  Also run Create-DesktopShortcut.ps1.

.PARAMETER Web
  Also install and build the phone app in .\web (needs Node.js 20+).
#>
param(
    [switch]$Skills,
    [switch]$Shortcuts,
    [switch]$Web
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Test-Tool([string]$name) { return [bool](Get-Command $name -ErrorAction SilentlyContinue) }

Write-Host "Checking prerequisites..." -ForegroundColor Cyan
if (-not (Test-Tool "dotnet")) { throw ".NET 8 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0" }
$sdks = (& dotnet --list-sdks) -join "`n"
if ($sdks -notmatch "(?m)^8\.") { throw ".NET SDK 8.x not found (found: $($sdks -replace '\s+', ' ')). Install .NET 8 from https://dotnet.microsoft.com/download/dotnet/8.0" }

$agyKnown = Join-Path $env:LOCALAPPDATA "agy\bin\agy.exe"
if (-not ((Test-Tool "agy") -or (Test-Path $agyKnown))) {
    Write-Warning "agy (Antigravity CLI) was not found. Levity needs it to chat. Install it, or set AGY_PATH in .env."
}
if ($Web -and -not (Test-Tool "npm")) { throw "Node.js 20+ is required for -Web. Install it from https://nodejs.org" }

if (-not (Test-Path ".env")) {
    Copy-Item ".env.example" ".env"
    Write-Host "Created .env from .env.example. Edit it if you need to." -ForegroundColor Green
}

Write-Host "Building Levity..." -ForegroundColor Cyan
& dotnet publish -c Release -r win-x64 --self-contained false -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
Copy-Item ".env" (Join-Path "publish" ".env") -Force   # Levity reads .env next to Levity.exe

if ($Web) {
    Write-Host "Building the phone app..." -ForegroundColor Cyan
    Push-Location web
    try { npm ci; npm run build } finally { Pop-Location }
    Write-Host "Phone app built in .\web\dist. Host that folder on any static host (see docs\RUNNING.md)." -ForegroundColor Green
}

if ($Skills) { & (Join-Path $PSScriptRoot "Install-Skills.ps1") }
if ($Shortcuts) { & (Join-Path $PSScriptRoot "Create-DesktopShortcut.ps1") }

Write-Host ""
Write-Host "Done. Start Levity with:  .\publish\Levity.exe   (or .\run.ps1). Hotkey: Win + Alt + A" -ForegroundColor Green
