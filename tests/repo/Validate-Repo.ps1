<#
.SYNOPSIS
  Repository checks that no unit test covers: scripts, skills, config template, docs and hygiene.
  Exits non-zero and lists every problem if anything fails. Run from anywhere: pwsh tests/repo/Validate-Repo.ps1
#>
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $root
$failures = [System.Collections.Generic.List[string]]::new()
$checks = 0

function Check([string]$name, [scriptblock]$body) {
    $script:checks++
    try {
        $problems = @(& $body)
        if ($problems.Count -gt 0) {
            foreach ($p in $problems) { $failures.Add("[$name] $p") }
            Write-Host "FAIL  $name" -ForegroundColor Red
        } else { Write-Host "ok    $name" -ForegroundColor Green }
    } catch {
        $failures.Add("[$name] threw: $($_.Exception.Message)")
        Write-Host "FAIL  $name (threw)" -ForegroundColor Red
    }
}

function TrackedFiles { & git -C $root ls-files 2>$null | Where-Object { $_ } }

Check "every PowerShell script parses" {
    Get-ChildItem $root -Recurse -Filter *.ps1 -File |
        Where-Object { $_.FullName -notmatch '\\(node_modules|bin|obj|publish|\.wrangler|dist)\\' } |
        ForEach-Object {
            $errs = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$errs)
            foreach ($e in @($errs)) { "$($_.FullName.Substring($root.Path.Length + 1)):$($e.Extent.StartLineNumber) $($e.Message)" }
        }
}

Check "every skill has valid front matter and its referenced scripts exist" {
    foreach ($dir in Get-ChildItem (Join-Path $root "agent\skills") -Directory) {
        $file = Join-Path $dir.FullName "SKILL.md"
        if (-not (Test-Path $file)) { "$($dir.Name): SKILL.md is missing"; continue }
        $text = Get-Content $file -Raw
        if ($text -notmatch '(?s)^---\s*\r?\n(.*?)\r?\n---') { "$($dir.Name): no YAML front matter"; continue }
        $fm = $Matches[1]
        if ($fm -notmatch '(?m)^name:\s*(\S+)\s*$') { "$($dir.Name): front matter has no name" }
        elseif ($Matches[1] -ne $dir.Name) { "$($dir.Name): name '$($Matches[1])' does not match the folder" }
        if ($fm -notmatch '(?ms)^description:\s*(>-?\s*\r?\n\s+)?\S') { "$($dir.Name): front matter has no description" }
        # Only full paths into this skill's own folder count; "e.g. scripts/run.ps1" in prose is just an example.
        foreach ($m in [regex]::Matches($text, [regex]::Escape($dir.Name) + '[\\/]scripts[\\/]([A-Za-z0-9._-]+\.(?:ps1|py|sh))')) {
            if (-not (Test-Path (Join-Path $dir.FullName "scripts\$($m.Groups[1].Value)"))) { "$($dir.Name): SKILL.md mentions scripts\$($m.Groups[1].Value) which does not exist" }
        }
    }
}

Check "skills and scripts contain no machine-specific paths" {
    Get-ChildItem (Join-Path $root "agent") -Recurse -File | ForEach-Object {
        $t = Get-Content $_.FullName -Raw
        if ($t -match '(?i)C:\\Users\\(?!Public|Default)[A-Za-z0-9._-]+') { "$($_.FullName.Substring($root.Path.Length + 1)) has a hard-coded user path: $($Matches[0])" }
        if ($t -match '(?m)\b[D-Z]:\\[A-Za-z]') { "$($_.FullName.Substring($root.Path.Length + 1)) has a hard-coded drive path" }
    }
}

Check "Install-Skills installs, skips, forces and uninstalls cleanly" {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("levity-skills-" + [guid]::NewGuid().ToString("N"))
    try {
        $target = Join-Path $tmp ".gemini\config\skills"
        $rules = Join-Path $tmp ".gemini\GEMINI.md"
        $expected = Get-ChildItem (Join-Path $root "agent\skills") -Directory | ForEach-Object Name

        & (Join-Path $root "Install-Skills.ps1") -Target $target *> $null
        foreach ($n in $expected) { if (-not (Test-Path (Join-Path $target "$n\SKILL.md"))) { "$n was not installed" } }
        if (-not (Select-String -Path $rules -Pattern "Fast Learner Protocol" -Quiet)) { "rule was not written" }

        & (Join-Path $root "Install-Skills.ps1") -Target $target *> $null
        $count = ([regex]::Matches((Get-Content $rules -Raw), [regex]::Escape("levity:fast-learner:start"))).Count
        if ($count -ne 1) { "running twice wrote the rule $count times" }

        Set-Content (Join-Path $target "fast-learner\SKILL.md") "tampered"
        & (Join-Path $root "Install-Skills.ps1") -Target $target *> $null
        if ((Get-Content (Join-Path $target "fast-learner\SKILL.md") -Raw).Trim() -ne "tampered") { "an existing skill was replaced without -Force" }
        & (Join-Path $root "Install-Skills.ps1") -Target $target -Force *> $null
        if ((Get-Content (Join-Path $target "fast-learner\SKILL.md") -Raw) -match "tampered") { "-Force did not replace the skill" }

        & (Join-Path $root "Install-Skills.ps1") -Target $target -Uninstall *> $null
        foreach ($n in $expected) { if (Test-Path (Join-Path $target $n)) { "$n was not removed" } }
        if ((Get-Content $rules -Raw) -match "Fast Learner") { "the rule was not removed" }
    } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

Check ".env.example, the code and the docs agree on configuration keys" {
    $example = Get-Content (Join-Path $root ".env.example")
    $exampleKeys = $example | Where-Object { $_ -match '^[A-Z][A-Z0-9_]*=' } | ForEach-Object { ($_ -split '=', 2)[0] }
    $docs = Get-Content (Join-Path $root "docs\RUNNING.md") -Raw

    $codeKeys = Get-ChildItem (Join-Path $root "Services"), (Join-Path $root "MainWindow.xaml.cs") -Recurse -Include *.cs -ErrorAction SilentlyContinue |
        ForEach-Object { [regex]::Matches((Get-Content $_.FullName -Raw), 'DotEnv\.Get(?:Double)?\("([A-Z0-9_]+)"') } |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $codeKeys += "LEVITY_SETTINGS_DIR", "LEVITY_ENV_FILE"   # read directly, not through DotEnv.Get

    foreach ($k in ($codeKeys | Sort-Object -Unique)) {
        if ($k -ne "LEVITY_ENV_FILE" -and $exampleKeys -notcontains $k) { "$k is read by the code but missing from .env.example" }
    }
    foreach ($k in $exampleKeys) {
        if ($docs -notmatch [regex]::Escape($k)) { "$k is in .env.example but not documented in docs/RUNNING.md" }
        $values = ($example | Where-Object { $_ -match "^$k=(.*)$" }) -replace "^$k=", ""
        if ($k -notin @("LEVITY_SIDEBAR_WIDTH") -and $values.Trim().Length -gt 0) { "$k has a value in .env.example; the template must ship empty" }
    }
}

Check "no secrets, personal config or local state are tracked" {
    $tracked = TrackedFiles
    if ($tracked) {
        foreach ($f in $tracked) {
            if ($f -match '(^|/)\.env$' -or $f -match '(^|/)\.env\.(?!example$)') { "$f must not be committed" }
            if ($f -match '(^|/)settings\.json$') { "$f must not be committed" }
            if ($f -match '\.log$') { "$f must not be committed" }
        }
        foreach ($f in $tracked | Where-Object { $_ -match '\.(cs|ts|tsx|js|mjs|ps1|md|json|toml|yml|yaml|html|css)$' -and $_ -notmatch 'package-lock\.json$|\.min\.' }) {
            $t = Get-Content (Join-Path $root $f) -Raw -ErrorAction SilentlyContinue
            if (-not $t) { continue }
            if ($t -match '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----') { "$f contains a private key" }
            if ($t -match '\b(ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9]{32,}|AKIA[0-9A-Z]{16})\b') { "$f contains something that looks like an API token" }
        }
    }
    if (-not (Select-String -Path (Join-Path $root ".gitignore") -Pattern '^\.env$' -Quiet)) { ".gitignore must ignore .env" }
    if (-not (Select-String -Path (Join-Path $root ".gitignore") -Pattern '^!\.env\.example$' -Quiet)) { ".gitignore must keep .env.example" }
}

Check "license and notice are in place" {
    $lic = Join-Path $root "LICENSE"
    if (-not (Test-Path $lic)) { "LICENSE is missing"; return }
    $t = Get-Content $lic -Raw
    if ($t -notmatch 'PolyForm Noncommercial License 1\.0\.0') { "LICENSE is not the PolyForm Noncommercial text" }
    if ($t -notmatch '(?m)^Required Notice: Copyright') { "LICENSE has no 'Required Notice: Copyright' line" }
    if ((Get-Content (Join-Path $root "README.md") -Raw) -notmatch '(?i)noncommercial') { "README does not mention the license terms" }
}

Check "docs link to files that exist" {
    foreach ($doc in "README.md", "docs\RUNNING.md") {
        $base = Split-Path (Join-Path $root $doc)
        $text = Get-Content (Join-Path $root $doc) -Raw
        foreach ($m in [regex]::Matches($text, '\]\((?!https?:|#|mailto:)([^)#\s]+)')) {
            $rel = $m.Groups[1].Value
            if (-not (Test-Path (Join-Path $base $rel))) { "$doc links to $rel which does not exist" }
        }
    }
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) problem(s) in $checks checks:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "All $checks repository checks passed." -ForegroundColor Green
