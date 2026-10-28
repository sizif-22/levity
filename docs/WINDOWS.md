# Windows tools: let the assistant look at and change Windows, safely

Without this, the assistant works on Windows by writing PowerShell every time: slow (a shell start costs about 8 seconds) and different on every
run. With it, the assistant calls **typed tools** instead: one call, a checked result, and every change written to a list you can undo from.

It is **off until you turn it on**. Press the **gear** in the top bar, choose **Windows tools**, and press **Turn on**. (The same Settings page also has **Connect your phone**.)

## What turning it on does

1. Levity starts a small tool server **inside itself**, listening on `127.0.0.1` only (nothing on your network can reach it), protected by a secret token.
2. Levity tells the assistant about it with the assistant's own command: `agy mcp add --header "Authorization: Bearer <token>" levity-windows http://127.0.0.1:<port>/mcp`. Levity never edits the assistant's configuration files by hand.
3. The assistant picks the tools up with its next message. The first message also carries a few lines about this PC (Windows build, hardware, drives, installed developer tools) so it does not have to discover them.

**Turn off** stops the server and runs `agy mcp remove levity-windows`. The server uses no memory while it is off.

## What keeps it safe

- **Typed, not free text.** No tool takes a command line. Names, paths, ports and package ids are checked against strict patterns; an unknown argument is refused.
- **Nothing is changed without a record.** Before every change Levity writes the old value (a registry value, a file, a service state) to the **change journal**. **Settings > Windows tools** lists the changes with a **Revert** button. Entries are kept for 30 days. Changes that cannot be undone (ending a program, running a task, clearing a print queue) are recorded and labelled as such.
- **Preview first.** Every changing tool accepts `dry_run`: it says exactly what would change and changes nothing, however the tool is called.
- **Core things are protected.** Essential Windows processes and services, the Windows folder and whole drives are refused. The firewall tool only removes rules Levity added; there is no free-form registry write tool at all.
- **Only this PC, only you.** Requests need the token; anything that looks like it came from a web page (an `Origin` header) or has a foreign `Host` header is refused; sizes and time are capped.

## Administrator changes

Some changes need administrator rights (service changes, the hosts file, firewall rules, Defender exclusions, machine-wide variables). Levity has no
background service with extra rights. Instead the assistant calls **run_elevated** for one tool; Levity starts an elevated copy of itself that runs exactly that
tool, and **Windows shows its approval prompt on the PC**. Nothing happens unless the person at the PC approves it; declining changes nothing.

This is only offered when Levity is installed in **Program Files**, because an elevated program in a folder other programs can write to could be replaced.
(For development you can set `LEVITY_ALLOW_ELEVATION_ANYWHERE=1`.)

## The tools

| Tool | Reads or changes | What it does |
|---|---|---|
| `machine_profile` | reads | Windows build, hardware, drives, installed developer tools and versions, shells, power plan (cached for a day) |
| `settings_get` | reads | wallpaper, theme, power plans, display |
| `settings_set` | changes | set the wallpaper (a local picture), light or dark theme, power plan |
| `startup_apps_list` | reads | programs that start with Windows, and whether each is enabled |
| `startup_apps_set` | changes | turn one start-up program on or off (what Task Manager's Startup tab does) |
| `environment_get` | reads | environment variables by scope |
| `environment_set` | changes | set or remove a user variable (machine scope needs administrator) |
| `path_check` | reads | PATH problems: missing folders, duplicates, entries that are files, too long |
| `path_edit` | changes | add, remove or clean entries in the user PATH |
| `disk_usage` | reads | free space per drive; the largest items in a folder (never follows junctions, never deletes) |
| `network_info` | reads | adapters, DNS lookup, ping, which program uses a port |
| `processes` | reads | heaviest programs by memory or CPU; find by name |
| `process_end` | changes | end one program by id and exact name (essential processes refused; cannot be undone) |
| `services_list` | reads | services with state and start type |
| `service_control` | changes, administrator | start, stop, restart or set the start type of a service (core services refused) |
| `scheduled_tasks_list` | reads | scheduled tasks, last result, next run |
| `scheduled_task_control` | changes | enable, disable or run a task |
| `winget_query` | reads | installed apps, available updates, search |
| `winget_change` | changes | install, upgrade or uninstall one app by exact package id |
| `printers_list` | reads | printers, waiting jobs, default |
| `printer_control` | changes | set the default printer; clear a stuck queue |
| `event_log` | reads | recent errors and warnings grouped by source and event id |
| `hosts_file` | changes (list reads), administrator | list, add or remove one hosts entry; the old file is kept for an exact revert |
| `firewall_rules` | reads | rules, filtered by name, program or port |
| `firewall_change` | changes, administrator | allow one program or TCP port inbound, or remove a rule Levity added |
| `defender_status` | reads | protection, signatures, last scans, exclusions |
| `defender_change` | changes, administrator | quick scan; add or remove one scan exclusion |
| `registry_read` | reads | one registry key (HKCU or HKLM) |
| `certificates` | reads | certificates and their expiry |
| `default_apps_get` | reads | which program opens web links, e-mail, PDF, text, images |
| `open_settings` | opens a page | opens a Windows Settings page so you can make a choice only you can make (Windows protects default apps, so they cannot be changed silently) |
| `changes_list` | reads | the change journal |
| `changes_revert` | changes | put back one journaled change |
| `run_elevated` | changes | run one administrator tool after you approve the Windows prompt |

Related: [Speed and memory](PERFORMANCE.md), [roadmap](ROADMAP.md).
