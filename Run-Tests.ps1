<#
.SYNOPSIS
  Runs every test suite, the same ones CI runs (.github/workflows/ci.yml).

.DESCRIPTION
  relay    typecheck, wrangler bundle, integration tests against the Worker running locally
  web      typecheck + production build, logic tests, component tests
  sidebar  the desktop UI (wwwroot) in jsdom
  desktop  build and test the C# services (real PowerShell, job control, crypto interop)
  repo     scripts, skills, .env.example, docs and hygiene checks

.PARAMETER Only
  Run just these suites, e.g. -Only relay,web.

.PARAMETER Install
  Run npm ci first in each Node project (needed on a fresh clone).
#>
param(
    [ValidateSet("relay", "web", "sidebar", "desktop", "repo")]
    [string[]]$Only = @("relay", "web", "sidebar", "desktop", "repo"),
    [switch]$Install
)

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$results = [ordered]@{}

function Step([string]$suite, [string]$title, [string]$dir, [scriptblock]$cmd) {
    if ($Only -notcontains $suite) { return }
    Write-Host ""
    Write-Host "== $suite : $title" -ForegroundColor Cyan
    Push-Location (Join-Path $root $dir)
    try {
        & $cmd
        $ok = ($LASTEXITCODE -eq 0)
    } catch { $ok = $false; Write-Host $_ -ForegroundColor Red }
    finally { Pop-Location }
    $key = "$suite / $title"
    $results[$key] = $ok
    if (-not $ok) { Write-Host "FAILED: $key" -ForegroundColor Red }
}

if ($Install) {
    foreach ($d in "relay", "web", "tests\sidebar") {
        if ((($Only -contains "relay") -and $d -eq "relay") -or (($Only -contains "web") -and $d -eq "web") -or (($Only -contains "sidebar") -and $d -eq "tests\sidebar")) {
            Write-Host "npm ci in $d" -ForegroundColor DarkGray
            Push-Location (Join-Path $root $d); npm ci --no-audit --no-fund; Pop-Location
        }
    }
}

Step "relay"   "typecheck"          "relay"         { npm run typecheck }
Step "relay"   "bundle (dry run)"   "relay"         { npm run bundle }
Step "relay"   "integration tests"  "relay"         { npm test }
Step "web"     "typecheck + build"  "web"           { npm run build }
Step "web"     "tests"              "web"           { npm test }
Step "sidebar" "UI tests"           "tests\sidebar" { npm test }
Step "desktop" "build"              "."             { dotnet build Levity.csproj -c Release --nologo -v q }
Step "desktop" "service tests"      "."             { dotnet test tests/Levity.Tests/Levity.Tests.csproj -c Release --nologo -v q }
Step "repo"    "repository checks"  "."             { pwsh -NoProfile -File tests/repo/Validate-Repo.ps1 }

Write-Host ""
Write-Host "==== Summary ====" -ForegroundColor Cyan
foreach ($k in $results.Keys) {
    if ($results[$k]) { Write-Host ("  pass  " + $k) -ForegroundColor Green } else { Write-Host ("  FAIL  " + $k) -ForegroundColor Red }
}
if ($results.Values -contains $false) { exit 1 }
Write-Host "All suites passed." -ForegroundColor Green
