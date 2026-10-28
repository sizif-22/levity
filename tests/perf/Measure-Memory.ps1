<#
.SYNOPSIS
  Measures how much memory Levity uses in each state and compares it with a budget.

.DESCRIPTION
  Starts a published Levity.exe with a throw-away settings folder, then samples the memory of everything it started:
  the host, every WebView2 process and the assistant (agy) with its helpers. It walks through the states a user goes through:

    visible       sidebar just opened
    hidden-5s     hidden in the tray, 5 seconds later
    hidden-long   hidden in the tray after the settle time (default 45 s)
    visible-again opened again from the tray

  Memory is the working set (what Task Manager roughly shows) in MB. Every process is counted, WebView2 shares pages between its
  processes so the total is a little higher than the real cost; the same method is used every time so numbers can be compared.

  Hiding and showing use the real hotkey (Win+Alt+A, or Ctrl+Alt+A if Windows reserved the first), so this needs a normal
  signed-in desktop. Only one Levity can run: stop the one in your tray first, or use -Attach to just take one reading.

.PARAMETER Exe          Levity.exe to test. Default: ..\..\publish\Levity.exe
.PARAMETER Budget       JSON file with the limits (default: budget.json next to this script)
.PARAMETER SettleSeconds  How long to wait in the hidden state before the long reading (default 45)
.PARAMETER AgentIdleSeconds  Passed to the app as LEVITY_AGENT_IDLE_SECONDS so the idle stop can be seen quickly (default: app default)
.PARAMETER Attach       Do not start anything: take one reading of the Levity that is already running
.PARAMETER Json         Also write the readings to this JSON file
.PARAMETER NoBudget     Print the table only, do not fail on the budget

.EXAMPLE
  .\Measure-Memory.ps1
  .\Measure-Memory.ps1 -Attach
#>
[CmdletBinding()]
param(
  [string]$Exe,
  [string]$Budget,
  [int]$SettleSeconds = 45,
  [int]$AgentIdleSeconds = 0,
  [switch]$Attach,
  [string]$Json,
  [switch]$NoBudget
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\..\publish\Levity.exe' }
if (-not $Budget) { $Budget = Join-Path $here 'budget.json' }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LevityPerfNative {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, int flags, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  /// <summary>True if the process owns a visible top-level window that is big enough to be the sidebar (not a tray helper).</summary>
  public static bool HasVisibleWindow(uint pid) {
    bool found = false;
    EnumWindows((h, l) => {
      uint owner; GetWindowThreadProcessId(h, out owner);
      if (owner != pid || !IsWindowVisible(h)) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      if (r.Right - r.Left >= 200 && r.Bottom - r.Top >= 200) { found = true; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@

function Get-Descendants([int]$RootId) {
  $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name
  $found = New-Object System.Collections.Generic.List[object]
  $queue = New-Object System.Collections.Generic.Queue[int]
  $queue.Enqueue($RootId)
  while ($queue.Count -gt 0) {
    $id = $queue.Dequeue()
    foreach ($p in ($all | Where-Object { $_.ParentProcessId -eq $id })) { $found.Add($p); $queue.Enqueue([int]$p.ProcessId) }
  }
  return $found
}

function Get-MB($ids) {
  $sum = 0
  foreach ($id in $ids) { $p = Get-Process -Id $id -ErrorAction SilentlyContinue; if ($p) { $sum += $p.WorkingSet64 } }
  return [math]::Round($sum / 1MB, 1)
}

function Test-Visible([int]$RootId) {
  if (-not (Get-Process -Id $RootId -ErrorAction SilentlyContinue)) { return $false }
  return [LevityPerfNative]::HasVisibleWindow([uint32]$RootId)
}

function Read-State([string]$Label, [int]$RootId) {
  $kids = Get-Descendants $RootId
  $web = @($kids | Where-Object { $_.Name -like 'msedgewebview2*' })
  $other = @($kids | Where-Object { $_.Name -notlike 'msedgewebview2*' -and $_.Name -notlike 'conhost*' })
  $hostMB = Get-MB @($RootId)
  $webMB = Get-MB ($web | ForEach-Object { [int]$_.ProcessId })
  $agentMB = Get-MB ($other | ForEach-Object { [int]$_.ProcessId })
  [pscustomobject]@{
    step = $Label; visible = (Test-Visible $RootId); hostMB = $hostMB; webviewMB = $webMB; webviewProcesses = $web.Count
    agentMB = $agentMB; agentProcesses = $other.Count; totalMB = [math]::Round($hostMB + $webMB + $agentMB, 1)
  }
}

function Send-Hotkey {
  # Win+Alt+A first; if Windows kept it, the app registered Ctrl+Alt+A instead, so try that when the window did not change.
  $VK_A = 0x41; $VK_LWIN = 0x5B; $VK_MENU = 0x12; $VK_CONTROL = 0x11; $UP = 2
  foreach ($mod in @($VK_LWIN, $VK_CONTROL)) {
    [LevityPerfNative]::keybd_event($mod, 0, 0, [UIntPtr]::Zero)
    [LevityPerfNative]::keybd_event($VK_MENU, 0, 0, [UIntPtr]::Zero)
    [LevityPerfNative]::keybd_event($VK_A, 0, 0, [UIntPtr]::Zero)
    [LevityPerfNative]::keybd_event($VK_A, 0, $UP, [UIntPtr]::Zero)
    [LevityPerfNative]::keybd_event($VK_MENU, 0, $UP, [UIntPtr]::Zero)
    [LevityPerfNative]::keybd_event($mod, 0, $UP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 900
    if ($script:visibleBefore -ne (Test-Visible $script:rootPid)) { return }
  }
}

function Set-Visible([bool]$Want) {
  for ($i = 0; $i -lt 3; $i++) {
    if ((Test-Visible $script:rootPid) -eq $Want) { return }
    $script:visibleBefore = Test-Visible $script:rootPid
    Send-Hotkey
    Start-Sleep -Milliseconds 600
  }
  if ((Test-Visible $script:rootPid) -ne $Want) { throw "Could not make the sidebar $(if ($Want) { 'visible' } else { 'hidden' }) with the hotkey." }
}

# ---- run ----
$readings = New-Object System.Collections.Generic.List[object]
$proc = $null
$script:rootPid = 0

if ($Attach) {
  $running = Get-Process Levity -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $running) { throw 'Levity is not running.' }
  $script:rootPid = $running.Id
  $readings.Add((Read-State 'attached' $script:rootPid))
} else {
  if (Get-Process Levity -ErrorAction SilentlyContinue) { throw 'Levity is already running. Stop it first (tray icon, Exit), or use -Attach.' }
  $exePath = (Resolve-Path $Exe).Path
  $settings = Join-Path ([IO.Path]::GetTempPath()) ('levity-perf-' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $settings | Out-Null
  $env:LEVITY_SETTINGS_DIR = $settings
  if ($AgentIdleSeconds -gt 0) { $env:LEVITY_AGENT_IDLE_SECONDS = "$AgentIdleSeconds" }
  try {
    $proc = Start-Process -FilePath $exePath -PassThru -WorkingDirectory (Split-Path $exePath)
    $script:rootPid = $proc.Id
    # wait for the sidebar to open by itself and the page to settle
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Visible $script:rootPid) -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300 }
    if (-not (Test-Visible $script:rootPid)) { throw 'The sidebar did not open within 30 seconds.' }
    Start-Sleep -Seconds 8
    $readings.Add((Read-State 'visible' $script:rootPid))

    Set-Visible $false
    Start-Sleep -Seconds 5
    $readings.Add((Read-State 'hidden-5s' $script:rootPid))

    Start-Sleep -Seconds $SettleSeconds
    $readings.Add((Read-State 'hidden-long' $script:rootPid))

    Set-Visible $true
    Start-Sleep -Seconds 4
    $readings.Add((Read-State 'visible-again' $script:rootPid))
  }
  finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Where-Object { -not (Get-Process -Id $_.Id -ErrorAction SilentlyContinue).Responding } | Out-Null
    Remove-Item -Recurse -Force $settings -ErrorAction SilentlyContinue
    Remove-Item Env:LEVITY_SETTINGS_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:LEVITY_AGENT_IDLE_SECONDS -ErrorAction SilentlyContinue
  }
}

$readings | Format-Table step, visible, hostMB, webviewMB, webviewProcesses, agentMB, agentProcesses, totalMB -AutoSize | Out-String | Write-Host
if ($Json) { $readings | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $Json }

# ---- budget ----
if ($NoBudget -or $Attach -or -not (Test-Path $Budget)) { return }
$limits = Get-Content $Budget -Raw | ConvertFrom-Json
$failed = $false
foreach ($r in $readings) {
  $limit = $limits.steps.$($r.step)
  if ($null -eq $limit) { continue }
  foreach ($k in 'totalMB', 'hostMB', 'webviewMB', 'agentMB') {
    $max = $limit.$k
    if ($null -ne $max -and $r.$k -gt $max) { Write-Host ("OVER  {0}: {1} is {2} MB, limit {3} MB" -f $r.step, $k, $r.$k, $max) -ForegroundColor Red; $failed = $true }
  }
}
if ($failed) { Write-Host 'Memory budget exceeded.' -ForegroundColor Red; exit 1 }
Write-Host 'Memory is within the budget.' -ForegroundColor Green
