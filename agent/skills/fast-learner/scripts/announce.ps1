<#
.SYNOPSIS
  Tells Levity what Fast Learner is doing, and keeps a record you can review later.

.DESCRIPTION
  Levity watches the agent's tool calls. When it sees this script it shows a notice in the sidebar
  (and on a paired phone, and as a Windows notification if the sidebar is hidden).
  Kinds skill, docs and notes are also appended to a log, so "notes for later" are never lost.

.PARAMETER Kind
  start  Fast Learner begins working on what it just learned.
  skill  A skill is being created or updated (give -Name).
  docs   Project documentation is being updated (give -Path if you know it).
  notes  A note is saved for later because the solution is not verified, or not worth a skill yet.
  done   Fast Learner has finished.

.PARAMETER Message
  One short sentence saying what was learned or changed.

.PARAMETER LogPath
  Where the log lives. Default: $env:LEVITY_LEARNED_LOG, else ..\learned.jsonl next to this script's skill.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("start", "skill", "docs", "notes", "done")]
    [string]$Kind,

    [string]$Message = "",
    [string]$Name = "",
    [string]$Path = "",
    [string]$LogPath = $(if ($env:LEVITY_LEARNED_LOG) { $env:LEVITY_LEARNED_LOG } else { Join-Path $PSScriptRoot "..\learned.jsonl" })
)

$ErrorActionPreference = "Stop"

if ($Kind -in "skill", "docs", "notes") {
    $entry = [ordered]@{
        time    = (Get-Date).ToString("o")
        kind    = $Kind
        message = $Message
        name    = $Name
        path    = $Path
        cwd     = (Get-Location).Path
    }
    $dir = Split-Path $LogPath -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $line = ($entry | ConvertTo-Json -Compress) + [Environment]::NewLine
    [System.IO.File]::AppendAllText($LogPath, $line, (New-Object System.Text.UTF8Encoding($false)))
}

$label = switch ($Kind) {
    "start" { "running" }
    "skill" { "saving skill" }
    "docs"  { "updating docs" }
    "notes" { "note saved for later" }
    "done"  { "done" }
}
Write-Output ("Fast Learner: {0}{1}" -f $label, $(if ($Message) { " - $Message" } else { "" }))
