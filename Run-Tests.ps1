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

.PARAMETER Changed
  Run only the suites your uncommitted changes touch (plus repo checks).

.PARAMETER Serial
  Run the suites one after another instead of in parallel (easier to read when debugging).

.PARAMETER Install
  Run npm ci first in each Node project (needed on a fresh clone).
#>
param(
    [ValidateSet("relay", "web", "sidebar", "desktop", "repo")]
    [string[]]$Only = @("relay", "web", "sidebar", "desktop", "repo"),
    [switch]$Changed,
    [switch]$Serial,
    [switch]$Install
)

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot

if ($Changed) {
    $files = @(git -C $root status --porcelain | ForEach-Object { ($_ -replace '^...', '') -replace '"', '' })
    $hit = [System.Collections.Generic.HashSet[string]]::new()
    [void]$hit.Add("repo")
    foreach ($f in $files) {
        switch -Regex ($f) {
            '^relay/'                                  { [void]$hit.Add("relay") }
            '^web/'                                    { [void]$hit.Add("web") }
            '^(wwwroot/|tests/sidebar/)'               { [void]$hit.Add("sidebar") }
            '\.(cs|xaml|csproj)$|^(Services|tests/Levity\.Tests)/' { [void]$hit.Add("desktop") }
        }
    }
    $Only = @($Only | Where-Object { $hit.Contains($_) })
    Write-Host "Changed suites: $($Only -join ', ')" -ForegroundColor DarkGray
}

if ($Install) {
    foreach ($d in "relay", "web", "tests\sidebar") {
        if ((($Only -contains "relay") -and $d -eq "relay") -or (($Only -contains "web") -and $d -eq "web") -or (($Only -contains "sidebar") -and $d -eq "tests\sidebar")) {
            Write-Host "npm ci in $d" -ForegroundColor DarkGray
            Push-Location (Join-Path $root $d); npm ci --no-audit --no-fund; Pop-Location
        }
    }
}

# The suites do not depend on each other, so each runs in its own process at the same time.
# Steps inside one suite stay in order (build before test).
$steps = @(
    @{ Suite = "relay";   Title = "typecheck";          Dir = "relay";         Cmd = "npm run typecheck" }
    @{ Suite = "relay";   Title = "bundle (dry run)";   Dir = "relay";         Cmd = "npm run bundle" }
    @{ Suite = "relay";   Title = "integration tests";  Dir = "relay";         Cmd = "npm test" }
    @{ Suite = "web";     Title = "typecheck + build";  Dir = "web";           Cmd = "npm run build" }
    @{ Suite = "web";     Title = "tests";              Dir = "web";           Cmd = "npm test" }
    @{ Suite = "sidebar"; Title = "UI tests";           Dir = "tests\sidebar"; Cmd = "npm test" }
    @{ Suite = "desktop"; Title = "build";              Dir = ".";             Cmd = "dotnet build Levity.csproj -c Release --nologo -v q" }
    @{ Suite = "desktop"; Title = "service tests";      Dir = ".";             Cmd = "dotnet test tests/Levity.Tests/Levity.Tests.csproj -c Release --nologo -v q" }
    @{ Suite = "repo";    Title = "repository checks";  Dir = ".";             Cmd = "pwsh -NoProfile -File tests/repo/Validate-Repo.ps1" }
)

$runSuite = {
    param($root, $suiteSteps)
    $out = [System.Text.StringBuilder]::new()
    $res = [ordered]@{}
    foreach ($st in $suiteSteps) {
        [void]$out.AppendLine("")
        [void]$out.AppendLine("== $($st.Suite) : $($st.Title)")
        Push-Location (Join-Path $root $st.Dir)
        try {
            $text = Invoke-Expression "$($st.Cmd) 2>&1" | Out-String
            $ok = ($LASTEXITCODE -eq 0)
            [void]$out.Append($text)
        } catch { $ok = $false; [void]$out.AppendLine($_.ToString()) }
        finally { Pop-Location }
        $key = "$($st.Suite) / $($st.Title)"
        $res[$key] = $ok
        if (-not $ok) { [void]$out.AppendLine("FAILED: $key") }
    }
    [pscustomobject]@{ Output = $out.ToString(); Results = $res }
}

$suites = @($steps | Where-Object { $Only -contains $_.Suite } | ForEach-Object { $_.Suite } | Select-Object -Unique)
$results = [ordered]@{}
$watch = [System.Diagnostics.Stopwatch]::StartNew()

if ($Serial -or $suites.Count -le 1) {
    foreach ($s in $suites) {
        $r = & $runSuite $root @($steps | Where-Object { $_.Suite -eq $s })
        Write-Host $r.Output
        foreach ($k in $r.Results.Keys) { $results[$k] = $r.Results[$k] }
    }
} else {
    $jobs = foreach ($s in $suites) {
        Start-Job -ScriptBlock $runSuite -ArgumentList $root, @($steps | Where-Object { $_.Suite -eq $s })
    }
    foreach ($j in $jobs) {
        $r = Receive-Job $j -Wait -AutoRemoveJob
        Write-Host $r.Output
        foreach ($k in $r.Results.Keys) { $results[$k] = $r.Results[$k] }
    }
}

Write-Host ""
Write-Host "==== Summary ($([int]$watch.Elapsed.TotalSeconds) s) ====" -ForegroundColor Cyan
foreach ($k in $results.Keys) {
    if ($results[$k]) { Write-Host ("  pass  " + $k) -ForegroundColor Green } else { Write-Host ("  FAIL  " + $k) -ForegroundColor Red }
}
if ($results.Values -contains $false) { exit 1 }
Write-Host "All suites passed." -ForegroundColor Green
