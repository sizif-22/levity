<#
.SYNOPSIS
  Installs Levity's agent skills and the Fast Learner rule for the Antigravity CLI (agy).

.DESCRIPTION
  Copies every folder under .\agent\skills into the agent's global skills folder and adds the
  Fast Learner rule to the global GEMINI.md. Safe to run again: existing skills are only replaced
  with -Force, and the rule is written between markers so it is never duplicated.

.PARAMETER Target
  Skills folder. Default: $env:LEVITY_SKILLS_DIR, else %USERPROFILE%\.gemini\config\skills.

.PARAMETER Force
  Replace skills that already exist in the target.

.PARAMETER Uninstall
  Remove the skills listed in .\agent\skills and the rule block.
#>
param(
    [string]$Target = $(if ($env:LEVITY_SKILLS_DIR) { $env:LEVITY_SKILLS_DIR } else { Join-Path $env:USERPROFILE ".gemini\config\skills" }),
    [switch]$Force,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$skillsSrc = Join-Path $root "agent\skills"
$ruleSrc = Join-Path $root "agent\fast-learner.rule.md"
$rulesFile = Join-Path (Split-Path (Split-Path $Target -Parent) -Parent) "GEMINI.md"
$start = "<!-- levity:fast-learner:start -->"
$end = "<!-- levity:fast-learner:end -->"

function Remove-RuleBlock([string]$text) {
    $pattern = "(?s)\r?\n?" + [regex]::Escape($start) + ".*?" + [regex]::Escape($end) + "\r?\n?"
    return [regex]::Replace($text, $pattern, "")
}

if ($Uninstall) {
    foreach ($dir in Get-ChildItem $skillsSrc -Directory) {
        $dest = Join-Path $Target $dir.Name
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force; Write-Host "Removed $($dir.Name)" }
    }
    if (Test-Path $rulesFile) {
        $text = Get-Content $rulesFile -Raw
        Set-Content $rulesFile (Remove-RuleBlock $text).TrimEnd() -NoNewline
        Write-Host "Removed the Fast Learner rule from $rulesFile"
    }
    return
}

New-Item -ItemType Directory -Force $Target | Out-Null
foreach ($dir in Get-ChildItem $skillsSrc -Directory) {
    $dest = Join-Path $Target $dir.Name
    if ((Test-Path $dest) -and -not $Force) {
        Write-Host "Skipped $($dir.Name) (already installed; use -Force to replace)" -ForegroundColor Yellow
        continue
    }
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Copy-Item $dir.FullName $dest -Recurse
    Write-Host "Installed $($dir.Name)" -ForegroundColor Green
}

$rule = (Get-Content $ruleSrc -Raw).Trim()
$block = "$start`r`n$rule`r`n$end"
$existing = if (Test-Path $rulesFile) { Get-Content $rulesFile -Raw } else { "" }
$updated = (Remove-RuleBlock $existing).TrimEnd()
if ($updated.Length -gt 0) { $updated += "`r`n`r`n" }
Set-Content $rulesFile ($updated + $block + "`r`n") -NoNewline
Write-Host "Fast Learner rule written to $rulesFile" -ForegroundColor Green
Write-Host "Done. Start a new agent session for the skills to load."
